using Azure.Storage.Queues.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FHIRBulkImport;

/// <summary>
/// Queue-triggered function that re-processes failed FHIR bundle chunks.
/// Messages arrive from the retry queue populated by the event-grid importers.
/// </summary>
public static class RetryProcessor
{
    [FunctionName("RetryProcessor")]
    public static async Task Run(
        [QueueTrigger("fhir-retry-queue", Connection = "FBI-STORAGEACCT")] QueueMessage message,
        ILogger log)
    {
        RetryMessage? msg;
        try
        {
            msg = JsonConvert.DeserializeObject<RetryMessage>(message.MessageText);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Retry] Could not deserialize queue message");
            return;
        }

        if (msg is null) return;

        log.LogInformation("[Retry] Processing correlationId={Id} blob={Blob} chunk={Chunk} attempt={Attempt}",
            msg.CorrelationId, msg.BlobName, msg.ChunkIndex, message.DequeueCount);

        try
        {
            await using var stream = await StorageUtils.OpenBlobStreamAsync(msg.Container, msg.BlobName);
            var content            = await new StreamReader(stream).ReadToEndAsync();

            JObject bundle;

            if (msg.BlobName.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)
             || msg.BlobName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                // Re-read the specific chunk by line offset
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                   .Skip((msg.ChunkIndex - 1) * FHIRUtils.MaxBundleSize)
                                   .Take(FHIRUtils.MaxBundleSize)
                                   .Select(l => JObject.Parse(l.Trim()))
                                   .ToList();
                bundle = new JObject
                {
                    ["resourceType"] = "Bundle",
                    ["type"]         = "batch",
                    ["entry"]        = new JArray(lines.Select(r => new JObject
                    {
                        ["resource"] = r,
                        ["request"]  = new JObject { ["method"] = "POST", ["url"] = r["resourceType"]?.ToString() ?? "Resource" }
                    }))
                };
            }
            else
            {
                var fullBundle = JObject.Parse(content);
                bundle = FHIRUtils.SplitBundle(fullBundle)
                                  .Skip(msg.ChunkIndex - 1)
                                  .FirstOrDefault() ?? fullBundle;
            }

            var (success, body, status) = await FHIRUtils.PostBundleAsync(bundle, log);
            var (resources, errors)     = FHIRUtils.ParseBundleResponse(body);

            if (success)
            {
                log.LogInformation("[Retry] Success correlationId={Id}: {Resources} resources", msg.CorrelationId, resources);
                await StorageUtils.WriteAuditAsync(msg.CorrelationId, msg.BlobName, "RetryCompleted",
                    resources, errors, $"chunk={msg.ChunkIndex}", log);
            }
            else
            {
                log.LogWarning("[Retry] Still failing correlationId={Id} status={Status}", msg.CorrelationId, status);
                await StorageUtils.WriteErrorAsync(msg.CorrelationId, msg.BlobName + $".retry{message.DequeueCount}", body, log);
                // Throw so the queue SDK re-queues up to maxDequeueCount, then moves to poison queue
                throw new InvalidOperationException($"FHIR POST returned {status}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            log.LogError(ex, "[Retry] Unexpected error for correlationId={Id}", msg.CorrelationId);
            throw;
        }
    }

    private sealed record RetryMessage(
        string BlobName,
        string Container,
        int    ChunkIndex,
        string CorrelationId);
}
