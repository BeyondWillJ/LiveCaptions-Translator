using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public class TranslateApiIsolationTests
{
    [Fact]
    public async Task ConcurrentProviders_KeepAuthorizationOnTheirOwnRequest()
    {
        var handler = new RecordingHandler();
        HttpClient previous = TranslateAPI.Client;
        TranslateAPI.Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var openRouter = new OpenRouterConfig { ApiKey = "router-secret" };
            var mtran = new MTranServerConfig
            {
                ApiKey = "mtran-secret",
                ApiUrl = "https://mtran.example/translate"
            };
            var routerSnapshot = Snapshot("OpenRouter", openRouter);
            var mtranSnapshot = Snapshot("MTranServer", mtran);

            await Task.WhenAll(
                TranslateAPI.TranslateAsync(routerSnapshot, "hello", default),
                TranslateAPI.TranslateAsync(mtranSnapshot, "world", default));

            Assert.Contains(handler.Requests, request =>
                request.Host == "openrouter.ai" && request.Authorization == "Bearer router-secret");
            Assert.Contains(handler.Requests, request =>
                request.Host == "mtran.example" && request.Authorization == "Bearer mtran-secret");
            Assert.DoesNotContain(handler.Requests, request =>
                request.Host == "openrouter.ai" && request.Authorization.Contains("mtran-secret"));
        }
        finally
        {
            TranslateAPI.Client.Dispose();
            TranslateAPI.Client = previous;
        }
    }

    [Fact]
    public async Task ContextAwareRequest_KeepsOldestToNewestAndCurrentLast()
    {
        var handler = new RecordingHandler();
        HttpClient previous = TranslateAPI.Client;
        TranslateAPI.Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var config = new OpenRouterConfig { ApiKey = "router-secret" };
            var contexts = new List<TranslationHistoryEntry>
            {
                Context("old source", "old translation"),
                Context("new source", "new translation")
            };
            var snapshot = new TranslationSettingsSnapshot(
                "OpenRouter", "zh-CN", "Translate to {0}", true, false, config, contexts);

            await TranslateAPI.TranslateAsync(snapshot, "current source", default);

            string body = Assert.Single(handler.Requests,
                request => request.Host == "openrouter.ai").Body;
            using JsonDocument json = JsonDocument.Parse(body);
            string[] contents = json.RootElement.GetProperty("messages")
                .EnumerateArray()
                .Select(message => message.GetProperty("content").GetString() ?? string.Empty)
                .ToArray();
            Assert.Equal(new[]
            {
                "Translate to zh-CN",
                "🔤 old source 🔤",
                "old translation",
                "🔤 new source 🔤",
                "new translation",
                "🔤 current source 🔤"
            }, contents);
        }
        finally
        {
            TranslateAPI.Client.Dispose();
            TranslateAPI.Client = previous;
        }
    }

    [Fact]
    public async Task HttpClientTimeout_ReturnsTimeoutError()
    {
        HttpClient previous = TranslateAPI.Client;
        TranslateAPI.Client = new HttpClient(new SlowHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        try
        {
            TranslationOutcome result = await TranslateAPI.TranslateOutcomeAsync(
                Snapshot("OpenRouter", new OpenRouterConfig()), "slow request", default);

            Assert.Equal(TranslationStatus.Failed, result.Status);
            Assert.Equal("Timeout", result.ErrorCode);
            Assert.DoesNotContain("slow request", result.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            TranslateAPI.Client.Dispose();
            TranslateAPI.Client = previous;
        }
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsHttpTimeout()
    {
        HttpClient previous = TranslateAPI.Client;
        TranslateAPI.Client = new HttpClient(new SlowHandler())
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TranslateAPI.TranslateAsync(
                    Snapshot("OpenRouter", new OpenRouterConfig()), "cancel request", cancellation.Token));
        }
        finally
        {
            TranslateAPI.Client.Dispose();
            TranslateAPI.Client = previous;
        }
    }

    [Fact]
    public async Task NetworkFailure_ReturnsSafeNetworkErrorOutcome()
    {
        HttpClient previous = TranslateAPI.Client;
        TranslateAPI.Client = new HttpClient(new NetworkFailureHandler());
        try
        {
            var config = new MTranServerConfig { ApiUrl = "http://127.0.0.1/translate" };
            TranslationOutcome result = await TranslateAPI.TranslateOutcomeAsync(
                Snapshot("MTranServer", config), "private source", default);

            Assert.Equal(TranslationStatus.Failed, result.Status);
            Assert.Equal("NetworkError", result.ErrorCode);
            Assert.DoesNotContain("private source", result.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("private payload", result.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            TranslateAPI.Client.Dispose();
            TranslateAPI.Client = previous;
        }
    }

    [Fact]
    public async Task HttpFailureAndMalformedResponse_ReturnTypedSafeDiagnostics()
    {
        await AssertOutcome(HttpStatusCode.InternalServerError, "provider-secret body", "HttpStatus");
        await AssertOutcome(HttpStatusCode.OK, "{}", "InvalidResponse");
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedBeforeUnboundedRead()
    {
        await AssertOutcome(HttpStatusCode.OK, new string('x', 1024 * 1024 + 1), "ResponseTooLarge");
    }

    private static async Task AssertOutcome(HttpStatusCode status, string body, string expectedCode)
    {
        HttpClient previous = TranslateAPI.Client;
        TranslateAPI.Client = new HttpClient(new FixedResponseHandler(status, body));
        try
        {
            TranslationOutcome result = await TranslateAPI.TranslateOutcomeAsync(
                Snapshot("OpenRouter", new OpenRouterConfig()), "private source", default);

            Assert.Equal(TranslationStatus.Failed, result.Status);
            Assert.Equal(expectedCode, result.ErrorCode);
            Assert.DoesNotContain("private source", result.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-secret", result.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            TranslateAPI.Client.Dispose();
            TranslateAPI.Client = previous;
        }
    }

    private static TranslationHistoryEntry Context(string source, string translation) => new()
    {
        Timestamp = string.Empty,
        TimestampFull = string.Empty,
        SourceText = source,
        TranslatedText = translation,
        TargetLanguage = "zh-CN",
        ApiUsed = "OpenRouter"
    };

    private static TranslationSettingsSnapshot Snapshot(string apiName, TranslateAPIConfig config) =>
        new(apiName, "zh-CN", "Translate to {0}", false, false, config, []);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentBag<(string Host, string Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            string body = request.Content == null ? string.Empty :
                await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.Host, authorization, body));
            await Task.Delay(25, cancellationToken);
            string json = request.RequestUri.Host == "openrouter.ai"
                ? "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"
                : "{\"result\":\"ok\"}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class NetworkFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("private payload"));
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
