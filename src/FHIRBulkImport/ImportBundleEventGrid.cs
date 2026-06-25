using System.IO.Compression;
using Azure.Messaging.EventGrid;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FHIRBulkImport;

/// <summary>
/// EventGrid-triggered function that fires when a JSON FHIR Bundle blob lands
/// in the "bundles" container.  Splits large bundles and POSTs each chunk to
/// the FHIR service with full audit/error tracking.
/// </summary>
public static class ImportBundleEventGrid
{
    [FunctionName("ImportBundleEventGrid")]
    public static async Task Run(
        [EventGridTrigger] EventGridEvent eventGridEvent,
        ILogger log)
    {
        if (eventGridEvent.EventType != "Microsoft.Storage.BlobCreated") return;

        var data         = eventGridEvent.Data.ToObjectFromJson<BlobCreatedEventData>();
        var (container, blobName) = ParseBlobUrl(data.Url);

        if (container != "bundles") return;
        if (!blobName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;

        var correlationId = Guid.NewGuid().ToString("N");
        log.LogInformation("[BundleImport] Start {Blob} correlationId={Id}", blobName, correlationId);

        try
        {
            await using var stream = await StorageUtils.OpenBlobStreamAsync(container, blobName);
            var content = await new StreamReader(stream).ReadToEndAsync();
            var bundle  = JObject.Parse(content);

            int chunkIndex = 0, totalResources = 0, totalErrors = 0;

            foreach (var chunk in FHIRUtils.SplitBundle(bundle))
            {
                chunkIndex++;
                log.LogInformation("[BundleImport] Posting chunk {Chunk} for {Blob}", chunkIndex, blobName);
                var (success, body, status) = await FHIRUtils.PostBundleAsync(chunk, log);
                var (resources, errors)     = FHIRUtils.ParseBundleResponse(body);
                totalResources += resources;
                totalErrors    += errors;

                if (!success)
                {
                    await StorageUtils.WriteErrorAsync(correlationId, blobName, body, log);
                    await StorageUtils.EnqueueRetryAsync(
                        new { blobName, container, chunkIndex, correlationId }, log);
                }
            }

            var finalStatus = totalErrors == 0 ? "Completed" : "CompletedWithErrors";
            await StorageUtils.WriteAuditAsync(correlationId, blobName, finalStatus,
                totalResources, totalErrors, null, log);

            log.LogInformation("[BundleImport] Done {Blob}: {Resources} resources, {Errors} errors",
                blobName, totalResources, totalErrors);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[BundleImport] Fatal error processing {Blob}", blobName);
            await StorageUtils.WriteAuditAsync(correlationId, blobName, "Failed", 0, 1, ex.Message, log);
            await StorageUtils.WriteErrorAsync(correlationId, blobName, ex.ToString(), log);
            throw;
        }
    }

    private static (string container, string blobName) ParseBlobUrl(string url)
    {
        var uri      = new Uri(url);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        return segments.Length == 2 ? (segments[0], segments[1]) : ("", url);
    }

    private sealed record BlobCreatedEventData(string Url, string ETag, string ContentType, long ContentLength);
}
