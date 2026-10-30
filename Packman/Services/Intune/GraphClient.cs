using Packman.Helpers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Packman.Services;

/// <summary>A Graph call that came back non-2xx. Carries status and body so callers can branch on them.</summary>
public sealed class GraphException : Exception
{
    public string Operation { get; }
    public int StatusCode { get; }
    public string Body { get; }
    public string? RequestId { get; }

    public GraphException(string operation, int statusCode, string body, string? requestId)
        : base($"{operation} failed (HTTP {statusCode}): {ExtractMessage(body)}")
    {
        Operation = operation;
        StatusCode = statusCode;
        Body = body;
        RequestId = requestId;
    }

    public bool IsForbidden => StatusCode is 401 or 403;
    public bool IsNotFound => StatusCode == 404;
    public bool IsServerError => StatusCode >= 500;

    /// <summary>Graph error bodies are {"error":{"code","message"}}; the message reads better than the JSON.</summary>
    public static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no response body";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.GetSafeString("message");
                var code = error.GetSafeString("code");
                if (!string.IsNullOrEmpty(message))
                    return string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
            }
        }
        catch (JsonException) { }
        return body.Length > 500 ? body[..500] + "…" : body;
    }
}

/// <summary>A mutation may have reached Graph, but its outcome could not be confirmed.</summary>
public sealed class GraphMutationUncertainException : Exception
{
    public string Operation { get; }
    public int? StatusCode { get; }
    public string? RequestId { get; }

    public GraphMutationUncertainException(string operation, int? statusCode = null, string? requestId = null, Exception? innerException = null)
        : base($"{operation} may have completed, but its result could not be confirmed" +
               (statusCode is { } status ? $" (HTTP {status})" : " because the connection failed or timed out") +
               ". Check the affected app or group in Intune or Entra before retrying to avoid duplicate changes.", innerException)
    {
        Operation = operation;
        StatusCode = statusCode;
        RequestId = requestId;
    }
}

/// <summary>Status and body of one Graph call.</summary>
public sealed class GraphResponse
{
    public int StatusCode { get; }
    public bool IsSuccess { get; }
    public string Body { get; }
    public string? RequestId { get; }
    /// <summary>An earlier attempt lost its result; a later rejection does not disprove that attempt succeeded.</summary>
    public bool HadUncertainAttempt { get; }

    internal GraphResponse(int statusCode, bool isSuccess, string body, string? requestId, bool hadUncertainAttempt = false)
    {
        StatusCode = statusCode;
        IsSuccess = isSuccess;
        Body = body;
        RequestId = requestId;
        HadUncertainAttempt = hadUncertainAttempt;
    }

    /// <summary>The parsed body; an undefined element when there is none.</summary>
    public JsonElement Json => string.IsNullOrWhiteSpace(Body) ? default : JsonSerializer.Deserialize<JsonElement>(Body);

    public GraphException ToException(string operation) => new(operation, StatusCode, Body, RequestId);
}

/// <summary>
/// The one HTTP path to Microsoft Graph: bearer token per call, JSON in and out,
/// throttling retries, and transient retries for reads and idempotent updates.
/// Ambiguous POST outcomes require callers to verify the change before retrying.
/// </summary>
public sealed class GraphClient
{
    public const string Beta = "https://graph.microsoft.com/beta";
    public const string MobileApps = Beta + "/deviceAppManagement/mobileApps";
    public const string Groups = Beta + "/groups";
    public const string Devices = Beta + "/devices";
    public const string Users = Beta + "/users";

    private const int MaxAttempts = 5;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);

    // One client per process: a new handler per call exhausts sockets on a long upload.
    // Settable only so tests can answer Graph with a fake handler.
    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions BodyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<Task<string>> _tokenProvider;

    public GraphClient(Func<Task<string>> tokenProvider)
        => _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    /// <summary>Acquires a token without calling anything, to fail early on a dead sign-in.</summary>
    public Task<string> GetTokenAsync() => _tokenProvider();

    public Task<GraphResponse> GetAsync(string url, string operation, CancellationToken ct = default, bool throwOnError = true)
        => SendAsync(HttpMethod.Get, url, null, operation, ct, throwOnError);

    public Task<GraphResponse> PostAsync(string url, object? body, string operation, CancellationToken ct = default, bool throwOnError = true)
        => SendAsync(HttpMethod.Post, url, body ?? "{}", operation, ct, throwOnError);

    public Task<GraphResponse> PatchAsync(string url, object body, string operation, CancellationToken ct = default, bool throwOnError = true)
        => SendAsync(HttpMethod.Patch, url, body, operation, ct, throwOnError);

    public Task<GraphResponse> DeleteAsync(string url, string operation, CancellationToken ct = default, bool throwOnError = true)
        => SendAsync(HttpMethod.Delete, url, null, operation, ct, throwOnError);

    /// <summary>
    /// Sends one request. <paramref name="body"/> is serialised as JSON, or sent verbatim
    /// when it is already a string. Uncertain mutations throw even when
    /// <paramref name="throwOnError"/> is false because there is no confirmed result.
    /// </summary>
    public async Task<GraphResponse> SendAsync(HttpMethod method, string url, object? body, string operation,
        CancellationToken ct = default, bool throwOnError = true)
    {
        // These PATCH callers replace properties with fixed values. POST creates resources
        // or invokes actions, so replaying a lost response can duplicate a committed change.
        var canReplay = method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options ||
                        method == HttpMethod.Put || method == HttpMethod.Patch || method == HttpMethod.Delete;
        var json = body is null ? null : body as string ?? JsonSerializer.Serialize(body, BodyOptions);
        var hadUncertainAttempt = false;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var token = await _tokenProvider();
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                if (!canReplay)
                    throw new GraphMutationUncertainException(operation, innerException: ex);
                if (attempt >= MaxAttempts) throw;
                hadUncertainAttempt = true;
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient timeout, not the caller's token.
                if (!canReplay)
                    throw new GraphMutationUncertainException(operation, innerException: ex);
                if (attempt >= MaxAttempts) throw;
                hadUncertainAttempt = true;
                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                var status = (int)response.StatusCode;

                if (!canReplay && (status == 408 || status >= 500))
                    throw new GraphMutationUncertainException(operation, status, Header(response, "request-id"));

                // HTTP 429 explicitly rejects the request, so it is safe to retry a POST.
                if ((status == 429 || (canReplay && (status is 503 or 504))) && attempt < MaxAttempts)
                {
                    if (status != 429) hadUncertainAttempt = true;
                    await Task.Delay(RetryAfter(response) ?? Backoff(attempt), ct);
                    continue;
                }

                var result = new GraphResponse(status, response.IsSuccessStatusCode, text, Header(response, "request-id"), hadUncertainAttempt);
                if (!result.IsSuccess && throwOnError)
                    throw result.ToException(operation);
                return result;
            }
        }
    }

    /// <summary>Walks a collection through @odata.nextLink, yielding each element of "value".</summary>
    public async IAsyncEnumerable<JsonElement> GetAllPagesAsync(string url, string operation,
        [EnumeratorCancellation] CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var count = 0;
        while (!string.IsNullOrEmpty(url))
        {
            var page = (await GetAsync(url, operation, ct)).Json;
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    count++;
                    yield return item;
                }
            }
            progress?.Report(count);
            url = page.ValueKind == JsonValueKind.Object ? page.GetSafeString("@odata.nextLink") : "";
        }
    }

    private static TimeSpan Backoff(int attempt)
        => TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble());

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null || delay <= TimeSpan.Zero) return null;
        return delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
