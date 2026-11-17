using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZeroVideo.App.Services;

var baseDir = Path.Combine(Path.GetTempPath(), "zerovideo-pixabay-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(baseDir);
try
{
    await CacheStopsDuplicateRequests();
    await RateLimitReportsResetWithoutFlooding();
    await PartialCachedFootageSurvives429();
    await ServerDirectedSingleRetry();
    await RepeatedRateLimitsEventuallyRecover();
    await InsufficientFootageDoesNotClaimCompletion();
    await OlderCacheCanRecoverWhenThrottled();
    Console.WriteLine("PASS: Pixabay 24h cache, HTTP 429, resume-safe footage and bounded retry");
}
finally
{
    try { Directory.Delete(baseDir, true); } catch { }
}

async Task CacheStopsDuplicateRequests()
{
    var handler = new PixabayStub();
    using var client = new HttpClient(handler);
    var service = new PixabayVideoService(client, Path.Combine(baseDir, "cache"), TimeSpan.Zero);
    var first = await service.AcquireAsync(["alpha"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    var second = await service.AcquireAsync(["alpha"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(first.Count == 1 && second.Count == 1, "Cached search must return usable footage.");
    Check(handler.ApiRequests == 1 && handler.VideoRequests == 1,
        "24-hour cache should prevent duplicate API calls and downloads.");
    Console.WriteLine("PASS: local 24-hour API and video cache");
}

async Task RateLimitReportsResetWithoutFlooding()
{
    var handler = new PixabayStub { AlwaysRateLimit = true, ResetHeader = "60" };
    using var client = new HttpClient(handler);
    var service = new PixabayVideoService(client, Path.Combine(baseDir, "429"), TimeSpan.Zero);
    try
    {
        await service.AcquireAsync(["rate"], "TEST-SECRET-KEY", "16:9", Console.WriteLine, CancellationToken.None);
        throw new Exception("Expected PixabayRateLimitException.");
    }
    catch (PixabayRateLimitException ex)
    {
        Check(ex.RetryAfter == TimeSpan.FromSeconds(60), "Must honor X-RateLimit-Reset as seconds.");
        Check(!ex.Message.Contains("TEST-SECRET-KEY", StringComparison.Ordinal),
            "Exceptions must never contain API keys.");
        Check(handler.ApiRequests == 1, "429 without permitted wait must not retry.");
    }
    Console.WriteLine("PASS: server reset metadata and no 429 retry flood");
}

async Task PartialCachedFootageSurvives429()
{
    var handler = new PixabayStub();
    using var client = new HttpClient(handler);
    var service = new PixabayVideoService(client, Path.Combine(baseDir, "partial"), TimeSpan.Zero);
    var warmed = await service.AcquireAsync(
        ["alpha", "beta", "gamma"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(warmed.Count == 3, "Warmup should save three licensed clips.");
    handler.RateLimitTerm = "rate";
    try
    {
        await service.AcquireAsync(
            ["rate", "gamma", "alpha", "beta"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
        throw new Exception("Rate-limited partial footage was incorrectly accepted as a complete video.");
    }
    catch (PixabayRateLimitException) { /* Expected; never render a three-clip video. */ }
    Check(service.RateLimitedDuringLastAcquire, "Caller must know footage was rate-limited.");
    Check(handler.ApiRequests == 4, "Only one new API request should be issued.");
    var cached = await service.AcquireAsync(
        ["gamma", "alpha", "beta"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(cached.Count == 3 && handler.ApiRequests == 4,
        "Downloaded clips and cached searches must remain usable after 429.");
    handler.RateLimitTerm = null;
    var recovered = await service.AcquireAsync(
        ["rate", "gamma", "alpha", "beta"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(recovered.Count == 4, "Retry after quota recovery should finish missing footage.");
    Check(recovered[0].SearchQuery == "rate" && recovered[1].SearchQuery == "gamma",
        "Fully recovered clips must follow narration search order.");
    Check(handler.ApiRequests == 5, "Resume should query only the previously blocked search.");
    Console.WriteLine("PASS: HTTP 429 pauses incomplete rendering while saved clips resume later");
}

async Task ServerDirectedSingleRetry()
{
    var handler = new PixabayStub { RateLimitFirstRequest = true, ResetHeader = "0" };
    using var client = new HttpClient(handler);
    var service = new PixabayVideoService(client, Path.Combine(baseDir, "retry"),
        TimeSpan.FromSeconds(3));
    var result = await service.AcquireAsync(["alpha"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(result.Count == 1 && handler.ApiRequests == 2,
        "One bounded retry after the provider's reset should recover.");
    Console.WriteLine("PASS: single retry after documented reset delay");
}

async Task RepeatedRateLimitsEventuallyRecover()
{
    var handler = new PixabayStub { RateLimitResponsesRemaining = 2, ResetHeader = "0" };
    using var client = new HttpClient(handler);
    var service = new PixabayVideoService(client, Path.Combine(baseDir, "repeated"),
        TimeSpan.FromSeconds(3));
    var results = await service.AcquireAsync(["alpha"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(results.Count == 1 && handler.ApiRequests == 3,
        "Should recover when two consecutive reset windows return 429, without rapid retry.");
    Console.WriteLine("PASS: repeated HTTP 429 windows recover with bounded waits");
}

async Task InsufficientFootageDoesNotClaimCompletion()
{
    var handler = new PixabayStub();
    using var client = new HttpClient(handler);
    var service = new PixabayVideoService(client, Path.Combine(baseDir, "insufficient"), TimeSpan.Zero);
    try
    {
        await service.AcquireAsync(
            ["alpha", "beta", "gamma", "delta", "epsilon", "zeta"],
            "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
        throw new Exception("Fewer than six distinct clips must not be considered complete.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Only 4/6"))
    {
        Console.WriteLine("PASS: insufficient distinct footage does not mark video complete");
    }
}

async Task OlderCacheCanRecoverWhenThrottled()
{
    var handler = new PixabayStub();
    using var client = new HttpClient(handler);
    var dataDir = Path.Combine(baseDir, "stale");
    var service = new PixabayVideoService(client, dataDir, TimeSpan.Zero);
    var original = await service.AcquireAsync(["alpha"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(original.Count == 1, "Warmup failed.");
    var hashed = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("TEST-KEY|alpha")));
    var cacheFile = Path.Combine(dataDir, "Pixabay", "ApiCache", hashed + ".json");
    File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddHours(-48));
    handler.AlwaysRateLimit = true;
    var fallback = await service.AcquireAsync(["alpha"], "TEST-KEY", "16:9", Console.WriteLine, CancellationToken.None);
    Check(fallback.Count == 1, "Stale local response may be used during API outage.");
    Check(handler.ApiRequests == 2, "Stale fallback should not generate repeated API requests.");
    Console.WriteLine("PASS: older valid cache remains useful during 429");
}

static void Check(bool result, string message)
{
    if (!result) throw new Exception("FAIL: " + message);
}

sealed class PixabayStub : HttpMessageHandler
{
    public int ApiRequests { get; private set; }
    public int VideoRequests { get; private set; }
    public bool AlwaysRateLimit { get; set; }
    public bool RateLimitFirstRequest { get; set; }
    public int RateLimitResponsesRemaining { get; set; }
    public string? RateLimitTerm { get; set; }
    public string ResetHeader { get; set; } = "60";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new Exception("Request URL was null.");
        if (uri.AbsolutePath.StartsWith("/api/videos", StringComparison.Ordinal))
        {
            ApiRequests++;
            var query = WebUtility.UrlDecode(
                Regex.Match(uri.Query, @"[?&]q=([^&]+)").Groups[1].Value);
            var limitedCount = RateLimitResponsesRemaining > 0;
            if (limitedCount) RateLimitResponsesRemaining--;
            var throttle = AlwaysRateLimit || limitedCount ||
                (RateLimitFirstRequest && ApiRequests == 1) ||
                query.Equals(RateLimitTerm, StringComparison.Ordinal);
            if (throttle)
            {
                var blocked = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                blocked.Headers.TryAddWithoutValidation("X-RateLimit-Reset", ResetHeader);
                blocked.Content = new StringContent("API rate limit exceeded");
                return Task.FromResult(blocked);
            }
            var id = query switch
            {
                "alpha" => 1101L,
                "beta" => 1102L,
                "gamma" => 1103L,
                _ => 1104L
            };
            var json = JsonSerializer.Serialize(new
            {
                hits = new[] {
                    new {
                        id,
                        pageURL = "https://pixabay.com/videos/example-" + id,
                        tags = query,
                        videos = new {
                            medium = new {
                                url = "https://cdn.pixabay.com/video/" + id + ".mp4",
                                width = 1280,
                                height = 720,
                                size = 8192L
                            }
                        }
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
        if (uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            VideoRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[8192])
            });
        }
        throw new Exception("Unexpected request in offline Pixabay test: " + uri.Host);
    }
}
