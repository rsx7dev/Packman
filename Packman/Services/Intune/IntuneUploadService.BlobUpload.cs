using Packman.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Packman.Services;

public partial class IntuneUploadService
{
    // Azure caps a block blob at 50,000 blocks and wants every id the same length, so the
    // index is padded to a width that covers the cap.
    private const int MaxBlocks = 50_000;
    private const string BlockIdFormat = "00000";
    private const int MaxBlockAttempts = 5;

    // Renew periodically, and earlier when the SAS expiry is approaching.
    private static readonly TimeSpan SasRenewalInterval = TimeSpan.FromMinutes(7);
    private static readonly TimeSpan SasExpiryMargin = TimeSpan.FromMinutes(1);

    // Per-request timeouts come from a linked token; the client itself never times out.
    // Settable only so tests can answer Azure Storage with a fake handler.
    internal static HttpClient AzureBlob { get; set; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string FileUrl(string appId, string contentVersionId, string fileId)
        => $"{GraphClient.MobileApps}/{appId}/microsoft.graph.win32LobApp/contentVersions/{contentVersionId}/files/{fileId}";

    private async Task<string> CreateContentVersionAsync(string appId, CancellationToken ct)
    {
        var url = $"{GraphClient.MobileApps}/{appId}/microsoft.graph.win32LobApp/contentVersions";
        var created = (await _graph.PostAsync(url, "{}", "Create content version", ct)).Json;
        return created.GetSafeString("id") is { Length: > 0 } id ? id : throw new Exception("Content version ID not returned");
    }

    private async Task<string> CreateFileEntryAsync(string appId, string contentVersionId, IntuneWinInfo intuneWin, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.mobileAppContentFile",
            ["name"] = intuneWin.FileName,
            ["size"] = intuneWin.UnencryptedContentSize,
            ["sizeEncrypted"] = intuneWin.EncryptedContentSize,
            ["manifest"] = null,
            ["isDependency"] = false,
        };
        var url = $"{GraphClient.MobileApps}/{appId}/microsoft.graph.win32LobApp/contentVersions/{contentVersionId}/files";
        var created = (await _graph.PostAsync(url, body, "Create file entry", ct)).Json;
        return created.GetSafeString("id") is { Length: > 0 } id ? id : throw new Exception("File ID not returned");
    }

    /// <summary>
    /// Polls the content file until its uploadState reaches "{stage}Success". "{stage}Pending"
    /// and an absent state keep waiting; Failed and TimedOut are terminal and throw
    /// <see cref="UploadStateException"/>; Graph's own 429/5xx are retried by the client.
    /// </summary>
    private async Task<JsonElement> WaitForUploadStateAsync(string fileUrl, string stage, TimeSpan timeout, TimeSpan interval,
        CancellationToken ct, Func<JsonElement, bool>? isReady = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var success = stage + "Success";
        var pending = stage + "Pending";

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var file = (await _graph.GetAsync(fileUrl, $"Read upload state ({stage})", ct)).Json;
            var state = file.GetSafeString("uploadState");

            if (state.Equals(success, StringComparison.OrdinalIgnoreCase) && (isReady?.Invoke(file) ?? true))
                return file;

            if (state.EndsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
                state.EndsWith("TimedOut", StringComparison.OrdinalIgnoreCase))
                throw new UploadStateException(stage, state);

            if (!string.IsNullOrEmpty(state) && !state.Equals(pending, StringComparison.OrdinalIgnoreCase))
                Debug.WriteLine($"Unexpected upload state '{state}' while waiting for {success}; still waiting.");

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out after {timeout.TotalMinutes:0} minutes waiting for {success} (last state: '{state}'). The app exists in Intune without content.");

            await Task.Delay(interval, ct);
        }
    }

    /// <summary>
    /// Streams the encrypted payload out of the .intunewin straight into block PUTs, then
    /// commits the block list. Reports progress in the 60-84 band.
    /// </summary>
    private async Task UploadToAzureStorageAsync(string sasUri, string fileUrl, IntuneWinInfo intuneWin,
        IUploadProgress? progress, UploadLogger log, CancellationToken ct)
    {
        var totalSize = intuneWin.EncryptedContentSize;
        int chunkSize = totalSize > 5L * 1024 * 1024 * 1024 ? 4 * 1024 * 1024 : 6 * 1024 * 1024;
        var totalChunks = (int)Math.Ceiling((double)totalSize / chunkSize);

        if (totalChunks > MaxBlocks)
            throw new Exception($"Package is too large to upload: it would need {totalChunks:N0} blocks and Azure allows {MaxBlocks:N0}.");

        progress?.UpdateProgress(60, $"Uploading package to Azure ({ByteSize.Format(totalSize)})...");

        using var archive = ZipFile.OpenRead(intuneWin.IntuneWinPath);
        var entry = archive.GetEntry(intuneWin.ContentEntryName)
            ?? throw new FileNotFoundException($"Payload entry '{intuneWin.ContentEntryName}' is missing from the .intunewin.");
        using var payload = entry.Open();

        var blockIds = new List<string>(totalChunks);
        var sasTimer = Stopwatch.StartNew();
        var currentSasUri = sasUri;

        async Task<string> GetSasUriAsync(bool forceRenewal, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (forceRenewal || SasNeedsRenewal(currentSasUri, sasTimer.Elapsed))
            {
                currentSasUri = await RenewSasUriAsync(fileUrl, currentSasUri, log, token);
                sasTimer.Restart();
            }
            return currentSasUri;
        }

        // One buffer for the run: a fresh 6 MB array per chunk goes straight to the LOH.
        var buffer = new byte[chunkSize];

        for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(chunkIndex.ToString(BlockIdFormat)));
            blockIds.Add(blockId);

            int bytesToRead = (int)Math.Min(chunkSize, totalSize - (long)chunkIndex * chunkSize);
            int read = 0;
            while (read < bytesToRead)
            {
                var n = await payload.ReadAsync(buffer.AsMemory(read, bytesToRead - read), ct);
                if (n == 0) throw new EndOfStreamException($"Payload ended after {(long)chunkIndex * chunkSize + read:N0} of {totalSize:N0} bytes.");
                read += n;
            }

            var percent = (int)((long)chunkIndex * 100 / totalChunks);
            var band = 60 + (int)((chunkIndex + 1.0) / totalChunks * 22);
            progress?.UpdateProgress(band, $"Uploading chunk {chunkIndex + 1}/{totalChunks} ({percent}%)");

            await PutBlockAsync(GetSasUriAsync, blockId, buffer, read, chunkIndex, totalChunks, ct);
        }

        progress?.UpdateProgress(83, "Finalizing Azure upload...");
        await PutBlockListAsync(GetSasUriAsync, blockIds, ct);
        progress?.UpdateProgress(84, "Package uploaded to Azure");
    }

    private static async Task PutBlockAsync(Func<bool, CancellationToken, Task<string>> getSasUriAsync, string blockId,
        byte[] buffer, int length, int chunkIndex, int totalChunks, CancellationToken ct)
    {
        var forceRenewal = false;
        var renewedAfterAuthFailure = false;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var sasUri = await getSasUriAsync(forceRenewal, ct);
            forceRenewal = false;
            var chunkUri = $"{sasUri}&comp=block&blockid={Uri.EscapeDataString(blockId)}";

            HttpStatusCode? status = null;
            string failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, chunkUri);
                request.Content = new ByteArrayContent(buffer, 0, length);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                request.Headers.Add("x-ms-blob-type", "BlockBlob");

                var timeout = chunkIndex == totalChunks - 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5 + attempt);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                using var response = await AzureBlob.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode) return;

                status = response.StatusCode;
                failure = AzureFailure(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { failure = "timed out"; }
            catch (HttpRequestException ex) { failure = $"network error ({ex.HttpRequestError})"; }

            Debug.WriteLine($"Block {chunkIndex + 1}/{totalChunks} attempt {attempt}/{MaxBlockAttempts} failed: {failure}");

            // Expired SAS tokens can reject a request after a long attempt. Refresh once
            // for an authentication failure, preserving this block's ID and exact bytes.
            forceRenewal = (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && !renewedAfterAuthFailure;
            if (forceRenewal) renewedAfterAuthFailure = true;
            if (!forceRenewal && IsPermanentAzureFailure(status))
                throw new Exception($"Azure Storage rejected block {chunkIndex + 1}: {failure}");

            if (attempt >= MaxBlockAttempts)
                throw new Exception($"Failed to upload block {chunkIndex + 1} after {MaxBlockAttempts} attempts: {failure}");

            var baseDelay = Math.Pow(2, attempt);
            await Task.Delay(TimeSpan.FromSeconds(baseDelay + baseDelay * Random.Shared.NextDouble()), ct);
        }
    }

    private static async Task PutBlockListAsync(Func<bool, CancellationToken, Task<string>> getSasUriAsync,
        List<string> blockIds, CancellationToken ct)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
        foreach (var blockId in blockIds)
            xml.Append("<Latest>").Append(blockId).Append("</Latest>");
        xml.Append("</BlockList>");
        var body = xml.ToString();

        var forceRenewal = false;
        var renewedAfterAuthFailure = false;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var sasUri = await getSasUriAsync(forceRenewal, ct);
            forceRenewal = false;

            HttpStatusCode? status = null;
            string failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, $"{sasUri}&comp=blocklist");
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = null;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(10));

                using var response = await AzureBlob.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode) return;
                status = response.StatusCode;
                failure = AzureFailure(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { failure = "timed out"; }
            catch (HttpRequestException ex) { failure = $"network error ({ex.HttpRequestError})"; }

            Debug.WriteLine($"Block list commit attempt {attempt}/{MaxBlockAttempts} failed: {failure}");
            forceRenewal = (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && !renewedAfterAuthFailure;
            if (forceRenewal) renewedAfterAuthFailure = true;
            if (!forceRenewal && IsPermanentAzureFailure(status))
                throw new Exception($"Azure Storage rejected the block list: {failure}");
            if (attempt >= MaxBlockAttempts)
                throw new Exception($"Failed to commit block list after {MaxBlockAttempts} attempts: {failure}");

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2), ct);
        }
    }

    private static bool SasNeedsRenewal(string sasUri, TimeSpan elapsed)
    {
        if (elapsed >= SasRenewalInterval) return true;
        if (!Uri.TryCreate(sasUri, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("The Azure upload URL is invalid.");

        foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = parameter.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("se", StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.TryParse(Uri.UnescapeDataString(parts[1]), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var expiry))
                return expiry <= DateTimeOffset.UtcNow + SasExpiryMargin;
        }
        return false;
    }

    private static bool IsPermanentAzureFailure(HttpStatusCode? status)
        => status is { } value && (int)value is >= 400 and < 500 &&
           value != HttpStatusCode.RequestTimeout && (int)value != 429;

    private static string AzureFailure(HttpResponseMessage response)
    {
        // Storage error bodies may echo a SAS signature or signed URL. Only log the
        // status and a validated service error code; never the raw body or request URI.
        var code = response.Headers.TryGetValues("x-ms-error-code", out var values) ? values.FirstOrDefault() : null;
        var safeCode = code is { Length: > 0 and <= 128 } && code.All(char.IsAsciiLetterOrDigit) ? $" ({code})" : "";
        return $"HTTP {(int)response.StatusCode} {response.StatusCode}{safeCode}";
    }

    /// <summary>Asks Intune for a fresh SAS URI. One retry; after that the upload cannot continue anyway.</summary>
    private async Task<string> RenewSasUriAsync(string fileUrl, string previousSasUri, UploadLogger log, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var outcomeUncertain = false;
            try
            {
                try
                {
                    // renewUpload has no request body. If its response is lost, observe
                    // the known file's renewal state instead of immediately posting again.
                    await _graph.SendAsync(HttpMethod.Post, $"{fileUrl}/renewUpload", null, "Renew upload URL", ct);
                }
                catch (GraphMutationUncertainException)
                {
                    outcomeUncertain = true;
                    log.Warning("Upload URL renewal response was inconclusive; checking the file's renewal state");
                }
                var file = await WaitForUploadStateAsync(fileUrl, "azureStorageUriRenewal", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), ct,
                    candidate => candidate.GetSafeString("azureStorageUri") != previousSasUri);
                var renewed = file.GetSafeString("azureStorageUri");
                if (string.IsNullOrEmpty(renewed))
                    throw new UploadStateException("the renewed Azure Storage URI", "azureStorageUriRenewalSuccess without a URI");
                log.Info("Upload URL renewed");
                return renewed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt == 1 && !outcomeUncertain)
            {
                log.Warning($"Upload URL renewal failed, retrying once ({ex.GetType().Name})");
            }
        }
    }

    private async Task CommitFileAsync(string fileUrl, EncryptionInfo encryption, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["fileEncryptionInfo"] = new Dictionary<string, object>
            {
                ["encryptionKey"] = encryption.EncryptionKey,
                ["macKey"] = encryption.MacKey,
                ["initializationVector"] = encryption.InitializationVector,
                ["mac"] = encryption.Mac,
                ["profileIdentifier"] = encryption.ProfileIdentifier,
                ["fileDigest"] = encryption.FileDigest,
                ["fileDigestAlgorithm"] = encryption.FileDigestAlgorithm,
            },
        };
        await _graph.PostAsync($"{fileUrl}/commit", body, "Commit file", ct);
    }

    private async Task CommitAppAsync(string appId, string contentVersionId, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.win32LobApp",
            ["committedContentVersion"] = contentVersionId,
        };

        var url = $"{GraphClient.MobileApps}/{appId}";
        IntuneFollowUpException Unconfirmed(Exception cause) => new(appId,
            $"Publish result unconfirmed. Intune may have accepted content version {contentVersionId}. " +
            "The app has been retained; check its content version and publishing state in Intune before retrying.", cause);

        GraphResponse response;
        try
        {
            response = await _graph.PatchAsync(url, body, "Publish app", ct, throwOnError: false);
        }
        catch (Exception ex)
        {
            // A lost response or cancellation can happen after Graph committed the
            // version. Never let the upload's rollback delete that potentially live app.
            throw Unconfirmed(ex);
        }
        if (response.IsSuccess) return;

        // A gateway failure may follow a committed change. Even a later rejection
        // cannot rule out an earlier attempt; verify the version before reporting it.
        if (response.HadUncertainAttempt || response.StatusCode == 408 || response.StatusCode >= 500)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var app = await _graph.GetAsync(url, "Verify publish", ct, throwOnError: false);
                if (app.IsSuccess && app.Json.GetSafeString("committedContentVersion") == contentVersionId)
                    return;
            }
            catch (Exception ex)
            {
                throw Unconfirmed(ex);
            }
            throw Unconfirmed(response.ToException("Publish app"));
        }

        throw response.ToException("Publish app");
    }
}
