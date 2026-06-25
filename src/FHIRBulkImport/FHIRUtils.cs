using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;

namespace FHIRBulkImport;

/// <summary>
/// Shared FHIR service client: token acquisition, HTTP send with retry/back-off,
/// and response parsing helpers.
/// </summary>
public static class FHIRUtils
{
    private static readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    // ── Configuration ────────────────────────────────────────────────────────

    public static string FhirUrl         => Env("FS-URL");
    public static string TenantName      => Env("FS-TENANT-NAME");
    public static string ClientId        => Env("FS-CLIENT-ID");
    public static string ClientSecret    => Env("FS-SECRET");
    public static string FhirResource    => Env("FS-RESOURCE", Env("FS-URL"));
    public static int    MaxRetries      => int.TryParse(Env("FBI-MAXRETRIES",  "3"),   out var v) ? v : 3;
    public static int    ThrottleDelayMs => int.TryParse(Env("FBI-THROTTLE-DELAY", "500"), out var v) ? v : 500;
    public static int    MaxBundleSize   => int.TryParse(Env("FBI-MAXBUNDLESIZE",  "500"), out var v) ? v : 500;
    public static int    ParallelPatients => int.TryParse(Env("FBI-PARALLELPATIENTS","10"), out var v) ? v : 10;

    private static string Env(string key, string fallback = "") =>
        Environment.GetEnvironmentVariable(key) ?? fallback;

    // ── Token cache ──────────────────────────────────────────────────────────

    private static string?    _cachedToken;
    private static DateTime   _tokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenLock = new(1, 1);

    public static async Task<string> GetFhirTokenAsync(ILogger log)
    {
        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-2))
                return _cachedToken;

            var app = ConfidentialClientApplicationBuilder
                .Create(ClientId)
                .WithClientSecret(ClientSecret)
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{TenantName}"))
                .Build();

            var result = await app.AcquireTokenForClient(new[] { $"{FhirResource}/.default" }).ExecuteAsync();
            _cachedToken = result.AccessToken;
            _tokenExpiry = result.ExpiresOn.UtcDateTime;
            return _cachedToken;
        }
        catch (MsalException ex)
        {
            log.LogError(ex, "Failed to acquire FHIR token");
            throw;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ── Retry pipeline ───────────────────────────────────────────────────────

    private static AsyncRetryPolicy<HttpResponseMessage> BuildRetryPolicy(ILogger log) =>
        Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(r => r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            .WaitAndRetryAsync(
                MaxRetries,
                attempt =>
                {
                    // Honour Retry-After header when present
                    return TimeSpan.FromMilliseconds(ThrottleDelayMs * Math.Pow(2, attempt - 1));
                },
                onRetry: (outcome, delay, attempt, _) =>
                {
                    var statusCode = outcome.Result?.StatusCode;
                    log.LogWarning("FHIR throttle/error {Status}. Retry {Attempt}/{Max} after {Delay}ms",
                        statusCode, attempt, MaxRetries, delay.TotalMilliseconds);
                });

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    public static async Task<(bool success, string body, int statusCode)> PostBundleAsync(
        JObject bundle, ILogger log)
    {
        var token   = await GetFhirTokenAsync(log);
        var policy  = BuildRetryPolicy(log);
        var content = new StringContent(bundle.ToString(), System.Text.Encoding.UTF8, "application/fhir+json");

        var response = await policy.ExecuteAsync(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, FhirUrl)
            {
                Content = new StringContent(bundle.ToString(), System.Text.Encoding.UTF8, "application/fhir+json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            return await _http.SendAsync(req);
        });

        var body = await response.Content.ReadAsStringAsync();
        bool ok  = response.IsSuccessStatusCode;

        if (!ok) log.LogError("FHIR POST failed [{Status}]: {Body}", (int)response.StatusCode, body[..Math.Min(500, body.Length)]);

        return (ok, body, (int)response.StatusCode);
    }

    public static async Task<(bool success, JToken? data)> GetFhirResourceAsync(
        string relativeUrl, ILogger log)
    {
        var token  = await GetFhirTokenAsync(log);
        var policy = BuildRetryPolicy(log);
        var url    = $"{FhirUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";

        var response = await policy.ExecuteAsync(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            return await _http.SendAsync(req);
        });

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            log.LogError("FHIR GET {Url} failed [{Status}]", url, (int)response.StatusCode);
            return (false, null);
        }
        return (true, JToken.Parse(body));
    }

    // ── Bundle splitter ──────────────────────────────────────────────────────

    /// <summary>Split a large bundle into chunks of at most <see cref="MaxBundleSize"/> entries.</summary>
    public static IEnumerable<JObject> SplitBundle(JObject bundle)
    {
        var entries = bundle["entry"] as JArray ?? new JArray();
        var bundleType = bundle["type"]?.ToString() ?? "batch";

        for (int i = 0; i < entries.Count; i += MaxBundleSize)
        {
            var chunk = new JObject
            {
                ["resourceType"] = "Bundle",
                ["type"]         = bundleType,
                ["entry"]        = new JArray(entries.Skip(i).Take(MaxBundleSize))
            };
            yield return chunk;
        }
    }

    // ── Response parsing ─────────────────────────────────────────────────────

    public static (int total, int errors) ParseBundleResponse(string responseBody)
    {
        try
        {
            var resp = JObject.Parse(responseBody);
            var entries = resp["entry"] as JArray ?? new JArray();
            int total  = entries.Count;
            int errors = entries.Count(e =>
            {
                var status = e["response"]?["status"]?.ToString() ?? "";
                return !status.StartsWith("2");
            });
            return (total, errors);
        }
        catch { return (0, 1); }
    }
}
