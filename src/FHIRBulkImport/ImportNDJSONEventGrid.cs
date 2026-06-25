using Azure.Messaging.EventGrid;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FHIRBulkImport;

/// <summary>
/// EventGrid-triggered function that fires when an NDJSON blob lands in the
/// "ndjson" container.  Reads each line as a FHIR resource, groups them into
/// transaction bundles of <see cref="FHIRUtils.MaxBundleSize"/>, and POSTs
/// each bundle to the FHIR service in parallel.
/// </summary>
public static class ImportNDJSONEventGrid
{
    [FunctionName("ImportNDJSONEventGrid")]
    public static async Task Run(
        [EventGridTrigger] EventGridEvent eventGridEvent,
        ILogger log)
    {
        if (eventGridEvent.EventType != "Microsoft.Storage.BlobCreated") return;

        var data              = eventGridEvent.Data.ToObjectFromJson<BlobCreatedEventData>();
        var (container, blob) = ParseBlobUrl(data.Url);

        if (container != "ndjson") return;
        if (!blob.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)
            && !blob.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;

        var correlationId = Guid.NewGuid().ToString("N");
        log.LogInformation("[NDJSONImport] Start {Blob} correlationId={Id}", blob, correlationId);

        int totalResources = 0, totalErrors = 0;

        try
        {
            await using var stream = await StorageUtils.OpenBlobStreamAsync(container, blob);
            using var reader       = new StreamReader(stream);

            var lineBuffer = new List<JObject>(FHIRUtils.MaxBundleSize);
            int chunkIndex = 0;

            async Task FlushBufferAsync()
            {
                if (lineBuffer.Count == 0) return;
                chunkIndex++;
                var bundle = WrapInBundle(lineBuffer);
                log.LogInformation("[NDJSONImport] Posting chunk {Chunk} ({Count} resources)", chunkIndex, lineBuffer.Count);
                var (success, body, _) = await FHIRUtils.PostBundleAsync(bundle, log);
                var (res, err)         = FHIRUtils.ParseBundleResponse(body);
                totalResources += res;
                totalErrors    += err;

                if (!success)
                {
                    await StorageUtils.WriteErrorAsync(correlationId, blob, body, log);
                    await StorageUtils.EnqueueRetryAsync(
                        new { blobName = blob, container, chunkIndex, correlationId }, log);
                }
                lineBuffer.Clear();
            }

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    lineBuffer.Add(JObject.Parse(line));
                }
                catch (Exception ex)
                {
                    log.LogWarning("[NDJSONImport] Skipping malformed line: {Err}", ex.Message);
                    totalErrors++;
                }

                if (lineBuffer.Count >= FHIRUtils.MaxBundleSize)
                    await FlushBufferAsync();
            }

            await FlushBufferAsync(); // flush remainder

            var status = totalErrors == 0 ? "Completed" : "CompletedWithErrors";
            await StorageUtils.WriteAuditAsync(correlationId, blob, status,
                totalResources, totalErrors, null, log);

            log.LogInformation("[NDJSONImport] Done {Blob}: {Resources} resources, {Errors} errors",
                blob, totalResources, totalErrors);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[NDJSONImport] Fatal error processing {Blob}", blob);
            await StorageUtils.WriteAuditAsync(correlationId, blob, "Failed", 0, 1, ex.Message, log);
            await StorageUtils.WriteErrorAsync(correlationId, blob, ex.ToString(), log);
            throw;
        }
    }

    private static JObject WrapInBundle(List<JObject> resources) => new()
    {
        ["resourceType"] = "Bundle",
        ["type"]         = "batch",
        ["entry"]        = new JArray(resources.Select(r => new JObject
        {
            ["resource"] = r,
            ["request"]  = new JObject
            {
                ["method"] = "POST",
                ["url"]    = r["resourceType"]?.ToString() ?? "Resource"
            }
        }))
    };

    private static (string container, string blobName) ParseBlobUrl(string url)
    {
        var uri      = new Uri(url);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        return segments.Length == 2 ? (segments[0], segments[1]) : ("", url);
    }

    private sealed record BlobCreatedEventData(string Url, string ETag, string ContentType, long ContentLength);
}
