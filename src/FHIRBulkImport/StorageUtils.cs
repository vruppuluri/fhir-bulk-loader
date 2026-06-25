using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace FHIRBulkImport;

/// <summary>Azure Storage helpers — blob read/write and audit/error logging.</summary>
public static class StorageUtils
{
    private static readonly string _connStr =
        Environment.GetEnvironmentVariable("FBI-STORAGEACCT")
        ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")
        ?? throw new InvalidOperationException("FBI-STORAGEACCT not configured");

    // ── Blob ─────────────────────────────────────────────────────────────────

    public static async Task<Stream> OpenBlobStreamAsync(string container, string blobName)
    {
        var client = GetBlobClient(container, blobName);
        return await client.OpenReadAsync();
    }

    public static async Task WriteBlobAsync(string container, string blobName, string content)
    {
        var client = GetBlobClient(container, blobName);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await client.UploadAsync(stream, overwrite: true);
    }

    public static async Task MoveBlobAsync(
        string srcContainer, string srcBlob,
        string dstContainer, string dstBlob,
        ILogger log)
    {
        try
        {
            var src = GetBlobClient(srcContainer, srcBlob);
            var dst = GetBlobClient(dstContainer, dstBlob);
            await dst.StartCopyFromUriAsync(src.Uri);
            await src.DeleteIfExistsAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed moving blob {Src}/{SrcBlob} → {Dst}/{DstBlob}",
                srcContainer, srcBlob, dstContainer, dstBlob);
        }
    }

    private static BlobClient GetBlobClient(string container, string blobName)
    {
        var svc = new BlobServiceClient(_connStr);
        var ctr = svc.GetBlobContainerClient(container);
        ctr.CreateIfNotExists();
        return ctr.GetBlobClient(blobName);
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    public static async Task WriteAuditAsync(
        string correlationId, string fileName, string status,
        int totalResources, int errorCount, string? detail, ILogger log)
    {
        var entry = new
        {
            correlationId,
            fileName,
            status,
            totalResources,
            errorCount,
            timestamp = DateTimeOffset.UtcNow,
            detail
        };
        var blobName = $"{DateTime.UtcNow:yyyy/MM/dd}/{correlationId}.json";
        try
        {
            await WriteBlobAsync("audit", blobName, JsonConvert.SerializeObject(entry, Formatting.Indented));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Audit write failed for {CorrelationId}", correlationId);
        }
    }

    // ── Error logging ────────────────────────────────────────────────────────

    public static async Task WriteErrorAsync(
        string correlationId, string fileName, string errorBody, ILogger log)
    {
        var blobName = $"{DateTime.UtcNow:yyyy/MM/dd}/{correlationId}-error.json";
        try
        {
            await WriteBlobAsync("errors", blobName, errorBody);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error blob write failed for {CorrelationId}", correlationId);
        }
    }

    // ── Queue (retry) ─────────────────────────────────────────────────────────

    public static async Task EnqueueRetryAsync(object message, ILogger log)
    {
        try
        {
            var qClient = new QueueClient(_connStr, "fhir-retry-queue",
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
            await qClient.CreateIfNotExistsAsync();
            await qClient.SendMessageAsync(JsonConvert.SerializeObject(message));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to enqueue retry message");
        }
    }
}
