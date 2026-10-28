using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Waikan.Models;
using Waikan.Services;

namespace Waikan.Tests;

public sealed class CloudSyncServiceTests
{
    [Fact]
    public async Task Sync_Posts_Pending_Events_With_Bearer_Key_And_Marks_Them_Synced()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();

            var offset = TimeSpan.FromHours(8);
            for (var index = 0; index < 3; index++)
            {
                var local = new DateTimeOffset(2026, 10, 29, 14, 21, index, offset);
                await database.InsertOccurrenceAsync(
                    local.ToUniversalTime(),
                    local,
                    OccurrenceRecord.AudioCaptureSourceMicrophone,
                    AppPaths.Keyword);
            }

            var handler = new StubHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"ok":true,"received":3,"accepted":3,"duplicates":0}""");
            using var http = new HttpClient(handler);
            var service = new CloudSyncService(database, log, http);

            var result = await service.SyncAsync(
                "waikan-view-01.pages.dev",
                "wk_test_key",
                CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal(3, result.Uploaded);
            Assert.Equal(3, result.Accepted);
            Assert.Equal(0, result.Pending);
            Assert.Equal(0, await database.GetPendingCountAsync());

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://waikan-view-01.pages.dev/api/events", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("wk_test_key", request.Headers.Authorization.Parameter);

            using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
            var events = document.RootElement.GetProperty("events");
            Assert.Equal(3, events.GetArrayLength());
            var first = events[0];
            Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("id").GetString()));
            Assert.Equal("2026-10-29", first.GetProperty("localDay").GetString());
            Assert.Equal(14, first.GetProperty("hour").GetInt32());
            Assert.Equal("microphone", first.GetProperty("source").GetString());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Unauthorized_Response_Leaves_Events_Pending()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();

            var local = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.FromHours(8));
            await database.InsertOccurrenceAsync(
                local.ToUniversalTime(),
                local,
                OccurrenceRecord.AudioCaptureSourceSystem,
                AppPaths.Keyword);

            var handler = new StubHandler();
            handler.Enqueue(HttpStatusCode.Unauthorized, """{"error":"密钥无效"}""");
            using var http = new HttpClient(handler);
            var service = new CloudSyncService(database, log, http);

            var result = await service.SyncAsync(
                "https://waikan-view-02.pages.dev/",
                "wk_wrong",
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(CloudSyncResultKind.Unauthorized, result.Kind);
            Assert.Equal(1, result.Pending);
            Assert.Equal(1, await database.GetPendingCountAsync());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Remote_Plain_Http_Endpoint_Is_Rejected()
    {
        Assert.False(CloudSyncService.TryNormalizeEndpoint("http://waikan-view-01.pages.dev", out _, out var error));
        Assert.Contains("https", error);
        Assert.True(CloudSyncService.TryNormalizeEndpoint("http://localhost:8788", out var local, out _));
        Assert.Equal("http://localhost:8788", local);
    }

    [Fact]
    public async Task Verify_Returns_Pair_Metadata_For_A_Valid_Key()
    {
        var root = TestInfrastructure.CreateTempDirectory();
        try
        {
            var log = TestInfrastructure.CreateLog(root);
            var database = new DatabaseService(Path.Combine(root, "waikan.db"), log);
            await database.InitializeAsync();

            var handler = new StubHandler();
            handler.Enqueue(
                HttpStatusCode.OK,
                """{"ok":true,"pair":"01 · 主力设备","timezone":"Asia/Shanghai","today":"2026-10-29"}""");
            using var http = new HttpClient(handler);
            var service = new CloudSyncService(database, log, http);

            var result = await service.VerifyAsync(
                "https://waikan-view-01.pages.dev/extra/path",
                "wk_valid",
                CancellationToken.None);

            Assert.True(result.Ok, result.Message);
            Assert.Equal("01 · 主力设备", result.Pair);
            Assert.Equal("Asia/Shanghai", result.TimeZone);
            Assert.Equal("2026-10-29", result.Today);
            Assert.Equal("https://waikan-view-01.pages.dev/api/verify", Assert.Single(handler.Requests).RequestUri!.ToString());
        }
        finally
        {
            TestInfrastructure.TryDeleteDirectory(root);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public List<string> Bodies { get; } = new();

        public void Enqueue(HttpStatusCode status, string body)
        {
            _responses.Enqueue((status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            (HttpStatusCode Status, string Body) response = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.InternalServerError, """{"error":"unexpected request"}""");
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
