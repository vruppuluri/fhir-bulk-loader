using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;

namespace FHIRBulkImport;

// ─────────────────────────────────────────────────────────────────────────────
// Data contracts
// ─────────────────────────────────────────────────────────────────────────────

public sealed record ExportQueryDefinition(
    string   Query,
    string   PatientReferenceField,
    string[] Include);

public sealed record ExportJobStatus(
    string              InstanceId,
    string              Status,
    int                 PatientsProcessed,
    int                 ResourcesWritten,
    DateTimeOffset      StartTime,
    DateTimeOffset?     EndTime,
    List<ExportOutput>  Output);

public sealed record ExportOutput(
    string ResourceType,
    string Url,
    int    Count,
    long   SizeBytes);

// ─────────────────────────────────────────────────────────────────────────────
// HTTP trigger — POST /api/$alt-export?code=<key>
// ─────────────────────────────────────────────────────────────────────────────

public static class AltExportTrigger
{
    [FunctionName("AltExportTrigger")]
    public static async Task<HttpResponseMessage> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "$alt-export")] HttpRequestMessage req,
        [DurableClient] IDurableOrchestrationClient starter,
        ILogger log)
    {
        string body = await req.Content!.ReadAsStringAsync();
        ExportQueryDefinition? queryDef;
        try
        {
            queryDef = JsonConvert.DeserializeObject<ExportQueryDefinition>(body);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Invalid export query definition");
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        if (queryDef is null || string.IsNullOrWhiteSpace(queryDef.Query))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var instanceId = await starter.StartNewAsync("ExportOrchestrator", null, queryDef);
        log.LogInformation("[Export] Started orchestration {InstanceId}", instanceId);

        return starter.CreateCheckStatusResponse(req, instanceId);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Durable orchestrator
// ─────────────────────────────────────────────────────────────────────────────

public static class ExportOrchestrator
{
    [FunctionName("ExportOrchestrator")]
    public static async Task<ExportJobStatus> RunOrchestrator(
        [OrchestrationTrigger] IDurableOrchestrationContext context,
        ILogger log)
    {
        var queryDef = context.GetInput<ExportQueryDefinition>()!;
        var startTime = context.CurrentUtcDateTime;

        // Step 1 — gather all patient IDs matching the root query
        var patientIds = await context.CallActivityAsync<List<string>>(
            "GatherPatientIds", queryDef);

        log.LogInformation("[Export] {Count} patients found for query '{Query}'",
            patientIds.Count, queryDef.Query);

        // Step 2 — fan out: export each patient's resources in parallel batches
        var allOutputs = new List<ExportOutput>();
        var batches    = patientIds
            .Select((id, i) => new { id, i })
            .GroupBy(x => x.i / FHIRUtils.ParallelPatients)
            .Select(g => g.Select(x => x.id).ToArray());

        foreach (var batch in batches)
        {
            var batchTasks = batch.Select(patientId =>
                context.CallActivityAsync<List<ExportOutput>>(
                    "ExportPatientResources",
                    new ExportPatientInput(patientId, queryDef.Include, context.InstanceId)));

            var batchResults = await Task.WhenAll(batchTasks);
            allOutputs.AddRange(batchResults.SelectMany(r => r));
        }

        // Aggregate outputs by resource type
        var aggregated = allOutputs
            .GroupBy(o => o.ResourceType)
            .Select(g => g.First() with
            {
                Count     = g.Sum(x => x.Count),
                SizeBytes = g.Sum(x => x.SizeBytes)
            })
            .ToList();

        // Step 3 — write completion manifest
        await context.CallActivityAsync("WriteExportManifest",
            new ExportManifestInput(context.InstanceId, aggregated, startTime));

        return new ExportJobStatus(
            InstanceId:        context.InstanceId,
            Status:            "Completed",
            PatientsProcessed: patientIds.Count,
            ResourcesWritten:  aggregated.Sum(o => o.Count),
            StartTime:         startTime,
            EndTime:           context.CurrentUtcDateTime,
            Output:            aggregated);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Activity: Gather patient IDs from FHIR query
// ─────────────────────────────────────────────────────────────────────────────

public static class GatherPatientIds
{
    [FunctionName("GatherPatientIds")]
    public static async Task<List<string>> Run(
        [ActivityTrigger] ExportQueryDefinition queryDef,
        ILogger log)
    {
        var ids = new List<string>();
        var nextUrl = queryDef.Query.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? queryDef.Query
            : queryDef.Query;

        // Page through FHIR search results
        string? cursor = queryDef.Query;
        while (cursor != null)
        {
            var (success, data) = await FHIRUtils.GetFhirResourceAsync(cursor, log);
            if (!success || data is null) break;

            var entries = data["entry"] as JArray ?? new JArray();
            foreach (var entry in entries)
            {
                var refField = entry["resource"]?[queryDef.PatientReferenceField];
                var refId    = refField?["reference"]?.ToString()
                            ?? refField?.ToString();

                if (!string.IsNullOrEmpty(refId))
                {
                    // Extract logical ID from "Patient/abc123" or bare "abc123"
                    var id = refId.Contains('/') ? refId.Split('/').Last() : refId;
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }

            // Follow paging link
            cursor = data["link"]
                         ?.FirstOrDefault(l => l["relation"]?.ToString() == "next")
                         ?["url"]?.ToString();
        }

        return ids.Distinct().ToList();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Activity: Export all included resources for one patient
// ─────────────────────────────────────────────────────────────────────────────

public sealed record ExportPatientInput(string PatientId, string[] Include, string InstanceId);

public static class ExportPatientResources
{
    [FunctionName("ExportPatientResources")]
    public static async Task<List<ExportOutput>> Run(
        [ActivityTrigger] ExportPatientInput input,
        ILogger log)
    {
        var outputs  = new Dictionary<string, (List<JObject> items, long bytes)>(StringComparer.OrdinalIgnoreCase);
        var storConn = Environment.GetEnvironmentVariable("FBI-STORAGEACCT")
                    ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")!;

        foreach (var includeQuery in input.Include)
        {
            // Substitute $IDS placeholder
            var query   = includeQuery.Replace("$IDS", input.PatientId, StringComparison.OrdinalIgnoreCase);
            var resType = query.Split('?')[0];

            string? cursor = query;
            while (cursor != null)
            {
                var (success, data) = await FHIRUtils.GetFhirResourceAsync(cursor, log);
                if (!success || data is null) break;

                var entries = data["entry"] as JArray ?? new JArray();
                foreach (var entry in entries)
                {
                    var resource = entry["resource"] as JObject;
                    if (resource is null) continue;
                    var json  = resource.ToString(Formatting.None);
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(json);

                    if (!outputs.ContainsKey(resType))
                        outputs[resType] = (new List<JObject>(), 0);

                    var (list, totalBytes) = outputs[resType];
                    list.Add(resource);
                    outputs[resType] = (list, totalBytes + bytes);
                }

                cursor = data["link"]
                             ?.FirstOrDefault(l => l["relation"]?.ToString() == "next")
                             ?["url"]?.ToString();
            }
        }

        // Write NDJSON blobs per resource type
        var result = new List<ExportOutput>();
        foreach (var (resType, (items, totalBytes)) in outputs)
        {
            if (items.Count == 0) continue;
            var ndjson   = string.Join('\n', items.Select(r => r.ToString(Formatting.None)));
            var blobName = $"{input.InstanceId}/{resType}-{input.PatientId}.xndjson";
            await StorageUtils.WriteBlobAsync("export", blobName, ndjson);
            var storageAccount = storConn.Contains("AccountName=")
                ? ExtractAccountName(storConn)
                : "storageaccount";
            result.Add(new ExportOutput(
                ResourceType: resType,
                Url:          $"https://{storageAccount}.blob.core.windows.net/export/{blobName}",
                Count:        items.Count,
                SizeBytes:    totalBytes));
        }

        return result;
    }

    private static string ExtractAccountName(string connStr)
    {
        var part = connStr.Split(';').FirstOrDefault(p => p.StartsWith("AccountName=", StringComparison.OrdinalIgnoreCase));
        return part?.Split('=', 2).LastOrDefault() ?? "storageaccount";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Activity: Write export manifest JSON
// ─────────────────────────────────────────────────────────────────────────────

public sealed record ExportManifestInput(string InstanceId, List<ExportOutput> Output, DateTimeOffset StartTime);

public static class WriteExportManifest
{
    [FunctionName("WriteExportManifest")]
    public static async Task Run(
        [ActivityTrigger] ExportManifestInput input,
        ILogger log)
    {
        var manifest = new
        {
            transactionTime = DateTimeOffset.UtcNow,
            instanceId      = input.InstanceId,
            startTime       = input.StartTime,
            output          = input.Output
        };

        var blobName = $"{input.InstanceId}/_completed_run.xjson";
        await StorageUtils.WriteBlobAsync("export", blobName,
            JsonConvert.SerializeObject(manifest, Formatting.Indented));

        log.LogInformation("[Export] Manifest written for instance {Id}", input.InstanceId);
    }
}
