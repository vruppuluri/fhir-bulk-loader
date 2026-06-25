using System.IO.Compression;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FHIRBulkImport;

/// <summary>
/// EventGrid-triggered function that fires when a .zip blob lands in the
/// "zip" container.  Decompresses in-memory and dispatches each entry to
/// the correct importer (bundle JSON or NDJSON).
/// </summary>
public static class ImportZIPEventGrid
{
    [FunctionName("ImportZIPEventGrid")]
    public static async Task Run(
        [EventGridTrigger] EventGridEvent eventGridEvent,
        ILogger log)
    {
        if (eventGridEvent.EventType != "Microsoft.Storage.BlobCreated") return;

        var data              = eventGridEvent.Data.ToObjectFromJson<BlobCreatedEventData>();
        var (container, blob) = ParseBlobUrl(data.Url);

        if (container != "zip") return;
        if (!blob.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;

        var correlationId = Guid.NewGuid().ToString("N");
        log.LogInformation("[ZIPImport] Start {Blob} correlationId={Id}", blob, correlationId);

        int totalResources = 0, totalErrors = 0;

        try
        {
            await using var blobStream = await StorageUtils.OpenBlobStreamAsync(container, blob);
            using var zipArchive       = new ZipArchive(blobStream, ZipArchiveMode.Read);

            log.LogInformation("[ZIPImport] Archive contains {Count} entries", zipArchive.Entries.Count);

            // Process entries in parallel with bounded concurrency
            var semaphore = new SemaphoreSlim(4);
            var tasks = zipArchive.Entries
                .Where(e => e.Name.EndsWith(".json",   StringComparison.OrdinalIgnoreCase)
                         || e.Name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)
                         || e.Name.EndsWith(".jsonl",  StringComparison.OrdinalIgnoreCase))
                .Select(async entry =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        return await ProcessEntryAsync(entry, correlationId, log);
                    }
                    finally { semaphore.Release(); }
                });

            var results = await Task.WhenAll(tasks);
            totalResources = results.Sum(r => r.resources);
            totalErrors    = results.Sum(r => r.errors);

            var status = totalErrors == 0 ? "Completed" : "CompletedWithErrors";
            await StorageUtils.WriteAuditAsync(correlationId, blob, status,
                totalResources, totalErrors, null, log);

            log.LogInformation("[ZIPImport] Done {Blob}: {Resources} resources, {Errors} errors",
                blob, totalResources, totalErrors);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[ZIPImport] Fatal error processing {Blob}", blob);
            await StorageUtils.WriteAuditAsync(correlationId, blob, "Failed", 0, 1, ex.Message, log);
            await StorageUtils.WriteErrorAsync(correlationId, blob, ex.ToString(), log);
            throw;
        }
    }

    private static async Task<(int resources, int errors)> ProcessEntryAsync(
        ZipArchiveEntry entry, string correlationId, ILogger log)
    {
        var name = entry.Name;
        log.LogInformation("[ZIPImport] Processing entry {Entry}", name);

        using var entryStream = entry.Open();
        var content           = await new StreamReader(entryStream).ReadToEndAsync();

        int resources = 0, errors = 0;

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var bundle = JObject.Parse(content);
            foreach (var chunk in FHIRUtils.SplitBundle(bundle))
            {
                var (success, body, _) = await FHIRUtils.PostBundleAsync(chunk, log);
                var (r, e)             = FHIRUtils.ParseBundleResponse(body);
                resources += r; errors += e;
                if (!success)
                    await StorageUtils.WriteErrorAsync(correlationId, name, body, log);
            }
        }
        else // ndjson / jsonl
        {
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var buffer = new List<JObject>(FHIRUtils.MaxBundleSize);

            async Task FlushAsync()
            {
                if (buffer.Count == 0) return;
                var bundle = new JObject
                {
                    ["resourceType"] = "Bundle",
                    ["type"]         = "batch",
                    ["entry"]        = new JArray(buffer.Select(r => new JObject
                    {
                        ["resource"] = r,
                        ["request"]  = new JObject { ["method"] = "POST", ["url"] = r["resourceType"]?.ToString() ?? "Resource" }
                    }))
                };
                var (success, body, _) = await FHIRUtils.PostBundleAsync(bundle, log);
                var (r, e)             = FHIRUtils.ParseBundleResponse(body);
                resources += r; errors += e;
                if (!success) await StorageUtils.WriteErrorAsync(correlationId, name, body, log);
                buffer.Clear();
            }

            foreach (var line in lines)
            {
                try   { buffer.Add(JObject.Parse(line.Trim())); }
                catch { errors++; continue; }
                if (buffer.Count >= FHIRUtils.MaxBundleSize) await FlushAsync();
            }
            await FlushAsync();
        }

        return (resources, errors);
    }

    private static (string container, string blobName) ParseBlobUrl(string url)
    {
        var uri      = new Uri(url);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        return segments.Length == 2 ? (segments[0], segments[1]) : ("", url);
    }

    private sealed record BlobCreatedEventData(string Url, string ETag, string ContentType, long ContentLength);
}
