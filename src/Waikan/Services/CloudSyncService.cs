using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Waikan.Models;

namespace Waikan.Services;

public enum CloudSyncResultKind
{
    Success,
    NotConfigured,
    Unauthorized,
    InvalidResponse,
    NetworkError,
    ServerError,
}

public sealed record CloudVerifyResult(
    bool Ok,
    string Message,
    string? Pair = null,
    string? TimeZone = null,
    string? Today = null);

public sealed record CloudSyncResult(
    bool Ok,
    CloudSyncResultKind Kind,
    string Message,
    int Uploaded = 0,
    int Accepted = 0,
    int Duplicates = 0,
    int Pending = -1,
    DateTimeOffset? CompletedAt = null);

/// <summary>
/// Pushes locally stored occurrences to the paired Cloudflare Pages viewer.
/// The client key never leaves this machine except in the Authorization header.
/// </summary>
public sealed class CloudSyncService : IDisposable
{
    private const int BatchSize = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DatabaseService _database;
    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public CloudSyncService(DatabaseService database, LogService log, HttpClient? httpClient = null)
    {
        _database = database;
        _log = log;
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        if (_ownsHttpClient)
        {
            _http.Timeout = TimeSpan.FromSeconds(25);
        }
    }

    public static bool TryNormalizeEndpoint(string? value, out string endpoint, out string error)
    {
        endpoint = string.Empty;
        error = string.Empty;
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "请填写 Cloudflare 查看页地址。";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = "地址格式无效，应类似 https://waikan-view-01.pages.dev。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "地址缺少主机名。";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "出于安全考虑，远程查看页必须使用 https 地址。";
            return false;
        }

        endpoint = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    public async Task<CloudVerifyResult> VerifyAsync(
        string? endpointValue,
        string? keyValue,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeEndpoint(endpointValue, out var endpoint, out var error))
        {
            return new CloudVerifyResult(false, error);
        }

        var key = (keyValue ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return new CloudVerifyResult(false, "请填写配对密钥。");
        }

        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint + "/api/verify", key);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new CloudVerifyResult(false, "配对密钥无效，或这个密钥属于另一个站点。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new CloudVerifyResult(
                    false,
                    $"验证失败：HTTP {(int)response.StatusCode}{ReadServerError(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True)
            {
                return new CloudVerifyResult(
                    true,
                    "连接成功",
                    ReadString(root, "pair"),
                    ReadString(root, "timezone"),
                    ReadString(root, "today"));
            }

            return new CloudVerifyResult(false, "站点返回了无法识别的响应。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CloudVerifyResult(false, "连接超时，请检查网络或查看页地址。");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"云端连接失败：{ex.Message}");
            return new CloudVerifyResult(false, $"无法连接到站点：{ex.Message}");
        }
        catch (JsonException)
        {
            return new CloudVerifyResult(false, "站点返回的内容不是有效 JSON。");
        }
    }

    public async Task<CloudSyncResult> SyncAsync(
        string? endpointValue,
        string? keyValue,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeEndpoint(endpointValue, out var endpoint, out var error))
        {
            return new CloudSyncResult(false, CloudSyncResultKind.NotConfigured, error);
        }

        var key = (keyValue ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return new CloudSyncResult(false, CloudSyncResultKind.NotConfigured, "请填写配对密钥。");
        }

        var uploaded = 0;
        var accepted = 0;
        var duplicates = 0;

        try
        {
            while (true)
            {
                var pending = await _database
                    .GetPendingOccurrencesAsync(BatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (pending.Count == 0)
                {
                    break;
                }

                var payload = new
                {
                    events = pending.Select(ToPayload).ToArray(),
                };
                var json = JsonSerializer.Serialize(payload, JsonOptions);

                using var request = CreateRequest(HttpMethod.Post, endpoint + "/api/events", key);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return await FailureAsync(
                        CloudSyncResultKind.Unauthorized,
                        "配对密钥无效，或这个密钥属于另一个站点。",
                        uploaded,
                        accepted,
                        duplicates,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return await FailureAsync(
                        response.StatusCode >= HttpStatusCode.InternalServerError
                            ? CloudSyncResultKind.ServerError
                            : CloudSyncResultKind.InvalidResponse,
                        $"云端写入失败：HTTP {(int)response.StatusCode}{ReadServerError(body)}",
                        uploaded,
                        accepted,
                        duplicates,
                        cancellationToken).ConfigureAwait(false);
                }

                var batch = ParseBatchResponse(body);
                if (batch is null)
                {
                    return await FailureAsync(
                        CloudSyncResultKind.InvalidResponse,
                        "站点返回了无法识别的同步响应。",
                        uploaded,
                        accepted,
                        duplicates,
                        cancellationToken).ConfigureAwait(false);
                }

                var syncIds = pending
                    .Select(record => record.SyncId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToArray();
                if (syncIds.Length == 0)
                {
                    return await FailureAsync(
                        CloudSyncResultKind.InvalidResponse,
                        "本地记录缺少同步 ID，请重新打开应用以完成数据库迁移。",
                        uploaded,
                        accepted,
                        duplicates,
                        cancellationToken).ConfigureAwait(false);
                }

                await _database.MarkSyncedAsync(syncIds, cancellationToken).ConfigureAwait(false);

                uploaded += pending.Count;
                accepted += batch.Accepted;
                duplicates += batch.Duplicates;

                // A short page means this was the final batch. Loop once more so a
                // record created during the upload is not left behind.
                if (pending.Count < BatchSize)
                {
                    continue;
                }
            }

            var remaining = await _database
                .GetPendingCountAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = new CloudSyncResult(
                true,
                CloudSyncResultKind.Success,
                remaining == 0 ? "同步完成" : "已同步，仍有待上传记录",
                uploaded,
                accepted,
                duplicates,
                remaining,
                DateTimeOffset.UtcNow);
            _log.Info(
                $"云端同步完成：sent={uploaded}, accepted={accepted}, duplicates={duplicates}, pending={remaining}.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailureAsync(
                CloudSyncResultKind.NetworkError,
                "同步超时，请检查网络后重试。",
                uploaded,
                accepted,
                duplicates,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"云端同步网络错误：{ex.Message}");
            return await FailureAsync(
                CloudSyncResultKind.NetworkError,
                $"无法连接到站点：{ex.Message}",
                uploaded,
                accepted,
                duplicates,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("云端同步失败。", ex);
            return await FailureAsync(
                CloudSyncResultKind.ServerError,
                $"同步失败：{ex.Message}",
                uploaded,
                accepted,
                duplicates,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private async Task<CloudSyncResult> FailureAsync(
        CloudSyncResultKind kind,
        string message,
        int uploaded,
        int accepted,
        int duplicates,
        CancellationToken cancellationToken)
    {
        var pending = -1;
        try
        {
            pending = await _database.GetPendingCountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The error result is still useful when the pending count cannot be read.
        }

        return new CloudSyncResult(
            false,
            kind,
            message,
            uploaded,
            accepted,
            duplicates,
            pending);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string key)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    private static object ToPayload(OccurrenceRecord record)
    {
        return new
        {
            id = record.SyncId,
            at = record.LocalTime.ToString("O", CultureInfo.InvariantCulture),
            localDay = record.LocalDay,
            hour = record.LocalTime.Hour,
            source = record.Source,
        };
    }

    private static ParsedBatch? ParseBatchResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var ok)
                || ok.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            return new ParsedBatch(
                ReadInt(root, "accepted"),
                ReadInt(root, "duplicates"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadServerError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var value = ReadString(document.RootElement, "error");
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $" · {value}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && value.TryGetInt32(out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private sealed record ParsedBatch(int Accepted, int Duplicates);
}
