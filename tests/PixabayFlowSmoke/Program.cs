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
    await DurationScaledFootageSelection();
    await SceneMoodAndNarrationOrdering();
    await RejectWrongMoodAndUnrelatedTags();
    await SmallSceneShortfallCanRenderWithoutUnrelatedClips();
    await EmptySceneStillBlockedEvenAboveCoverageThreshold();
    await LargeSceneShortfallStillBlocked();
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
    catch (InvalidOperationException ex) when (ex.Message.Contains("4/6"))
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


async Task DurationScaledFootageSelection()
{
    foreach (var minutes in new[] { 1, 3, 5, 10, 15 })
    {
        int expected = minutes * 10;
        int planned = PixabayVideoService.TargetClipCount(TimeSpan.FromMinutes(minutes));
        int searches = PixabayVideoService.SuggestedQueryCount(planned);
        Check(planned == expected, "Target count mismatch for " + minutes + " minutes.");
        Check(searches == Math.Clamp((int)Math.Ceiling(expected / 5d), 6, 30),
            "Search plan size mismatch.");
        var handler = new PixabayStub { HitsPerQuery = 8 };
        using var http = new HttpClient(handler);
        var service = new PixabayVideoService(http,
            Path.Combine(baseDir, "duration-" + minutes), TimeSpan.Zero);
        var queries = Enumerable.Range(0, searches).Select(i => "topic-" + i).ToArray();
        var clips = await service.AcquireAsync(
            queries, "TEST-KEY", "16:9", null, CancellationToken.None, expected);
        Check(clips.Count == expected, "Not enough distinct clips.");
        Check(clips.Select(x => x.Id).Distinct().Count() == expected,
            "Duplicate clip IDs in the finished plan.");
        Check(handler.ApiRequests == searches && handler.VideoRequests == expected,
            "Every video was not sourced efficiently from one query result per topic.");
        var cached = await service.AcquireAsync(
            queries, "TEST-KEY", "16:9", null, CancellationToken.None, expected);
        Check(cached.Count == expected && handler.ApiRequests == searches &&
            handler.VideoRequests == expected, "Cached results were fetched again.");
        Console.WriteLine($"PASS: {minutes} min, {expected} unique shots from {searches} API requests");
    }
}

async Task SceneMoodAndNarrationOrdering()
{
    var fake = new PixabayStub { HitsPerQuery = 4 };
    fake.TagForHit = (query, hit) => query switch
    {
        "dark forest" when hit == 0 => "sunny forest vacation daylight",
        "dark forest" => "dark forest shadow night horror",
        "sunny garden" when hit == 0 => "garden horror shadow midnight",
        "sunny garden" => "happy sunny garden cheerful daylight",
        _ => query
    };
    using var http = new HttpClient(fake);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "scene-moods"), TimeSpan.Zero);
    var scenes = new[] {
        new PixabayFootageScene(1, "Someone walks into a frightening forest.",
            "dark forest", "ominous"),
        new PixabayFootageScene(2, "The ending is peaceful in a flowering garden.",
            "sunny garden", "upbeat")
    };
    var clips = await service.AcquireAsync(
        scenes.Select(x => x.SearchQuery), "TEST-KEY", "16:9", null,
        CancellationToken.None, 4, null, scenes);
    Check(clips.Count == 4, "Scene planner did not select four matching visuals.");
    Check(clips.Take(2).All(c => c.SearchQuery == "dark forest") &&
        clips.Skip(2).All(c => c.SearchQuery == "sunny garden"),
        "Scene clips are not in the same order as the narration.");
    Check(!clips.Any(c => c.Id == 2100 || c.Id == 2200),
        "Visual mood mismatch was accepted (sunny horror or dark celebration).");
    Check(clips.Select(x => x.Id).Distinct().Count() == 4,
        "Scene footage must not repeat identical Pixabay clip IDs.");
    Console.WriteLine("PASS: narration-ordered scenes select matching dark / upbeat footage");
}

async Task RejectWrongMoodAndUnrelatedTags()
{
    Check(PixabayVideoService.IsScriptVisualMatch(
        "dark forest", "forest shadows nighttime", "ominous"),
        "Dark relevant footage should be accepted.");
    Check(!PixabayVideoService.IsScriptVisualMatch(
        "dark forest", "sunny green forest vacation", "ominous"),
        "A scary documentary must reject bright vacation footage.");
    Check(!PixabayVideoService.IsScriptVisualMatch(
        "haunted hallway", "happy swimming beach", "ominous"),
        "A query must not accept unrelated search results.");
    var fake = new PixabayStub { HitsPerQuery = 4,
        TagForHit = (_, _) => "sunny ocean vacation daytime" };
    using var http = new HttpClient(fake);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "reject-mismatches"), TimeSpan.Zero);
    var scenes = new[] {
        new PixabayFootageScene(1, "A dark forest terrifies the villagers.",
            "dark forest", "ominous")
    };
    try
    {
        await service.AcquireAsync(["dark forest"], "TEST-KEY", "16:9",
            null, CancellationToken.None, 2, null, scenes);
        throw new Exception("Wrong-mood footage was accepted.");
    }
    catch (InvalidOperationException ex) when (
        ex.Message.Contains("no relevant", StringComparison.OrdinalIgnoreCase))
    {
        Check(fake.VideoRequests == 0,
            "Unrelated footage must not even be downloaded.");
    }
    Console.WriteLine("PASS: mismatched stock clips are rejected rather than rendered");
}

async Task SmallSceneShortfallCanRenderWithoutUnrelatedClips()
{
    // Reproduces the screenshot: 41 usable Pixabay clips out of 45 slots,
    // only two scene quotas slightly short. No unrelated replacements needed.
    var stub = new PixabayStub { HitsPerQuery = 7 };
    stub.HitsForQuery = q => q is "topic-0" or "topic-2" ? 3 : 7;
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "41-of-45"), TimeSpan.Zero);
    var scenes = Enumerable.Range(0, 10)
        .Select(i => new PixabayFootageScene(i + 1,
            "Narration about topic " + i, "topic-" + i, "neutral")).ToArray();
    var logs = new List<string>();
    var clips = await service.AcquireAsync(scenes.Select(x => x.SearchQuery),
        "TEST-KEY", "16:9", logs.Add, CancellationToken.None, 45, null, scenes);
    Check(clips.Count == 41 && service.SceneFootageShortfall == 4,
        "41/45 scene-covered clips should continue into rendering.");
    Check(clips.Select(x => x.Id).Distinct().Count() == clips.Count,
        "Scene shortage recovery must not fabricate duplicate downloaded footage.");
    Check(scenes.All(x => clips.Any(c => c.SearchQuery == x.SearchQuery)),
        "Every narration scene must retain at least one relevant clip.");
    Check(logs.Any(x => x.Contains("extend", StringComparison.OrdinalIgnoreCase)),
        "The app must explain that existing scene shots will be extended.");
    Console.WriteLine("PASS: 41/45 relevant clips covers all 10 scenes with four extended shot slots");
}

async Task EmptySceneStillBlockedEvenAboveCoverageThreshold()
{
    // 40/45 is 89% coverage, but an entire missing scene must not
    // get filled with unrelated footage from a different subject.
    var stub = new PixabayStub { HitsPerQuery = 7,
        HitsForQuery = q => q == "topic-0" ? 0 : 7 };
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "missing-scene"), TimeSpan.Zero);
    var scenes = Enumerable.Range(0, 10).Select(i =>
        new PixabayFootageScene(i + 1, "Scene " + i, "topic-" + i, "neutral")).ToArray();
    try
    {
        await service.AcquireAsync(scenes.Select(x => x.SearchQuery),
            "TEST-KEY", "16:9", null, CancellationToken.None, 45, null, scenes);
        throw new Exception("Completely missing narration scene must block.");
    }
    catch (InvalidOperationException e) when
        (e.Message.Contains("no relevant", StringComparison.OrdinalIgnoreCase))
    {
        Check(stub.VideoRequests == 40, "Only properly matched video files should download.");
        Console.WriteLine("PASS: a missing scene is not silently replaced with unrelated footage");
    }
}

async Task LargeSceneShortfallStillBlocked()
{
    var stub = new PixabayStub { HitsPerQuery = 5,
        HitsForQuery = q =>
            int.TryParse(q["topic-".Length..], out var i) && i is 0 or 2 or 4 or 6 ? 4 : 3 };
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "too-sparse"), TimeSpan.Zero);
    var scenes = Enumerable.Range(0, 10).Select(i =>
        new PixabayFootageScene(i + 1, "Scene " + i, "topic-" + i, "neutral")).ToArray();
    try
    {
        await service.AcquireAsync(scenes.Select(x => x.SearchQuery),
            "TEST-KEY", "16:9", null, CancellationToken.None, 45, null, scenes);
        throw new Exception("Low-coverage video must not be marked complete.");
    }
    catch (InvalidOperationException e) when
        (e.Message.Contains("below", StringComparison.OrdinalIgnoreCase))
    {
        Check(stub.VideoRequests == 34, "Downloaded footage should remain usable for retries.");
        Console.WriteLine("PASS: insufficient coverage does not produce a misleading finished MP4");
    }
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
    public int HitsPerQuery { get; set; } = 1;
    public Func<string, int, string>? TagForHit { get; set; }
    public Func<string, int>? HitsForQuery { get; set; }

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
            if (query.StartsWith("topic-", StringComparison.Ordinal) &&
                int.TryParse(query["topic-".Length..], out var topicIndex))
                id = 50000 + topicIndex * 100;
            if (query == "dark forest") id = 2100;
            if (query == "sunny garden") id = 2200;
            var json = JsonSerializer.Serialize(new
            {
                hits = Enumerable.Range(0, HitsForQuery?.Invoke(query) ?? HitsPerQuery).Select(i => new {
                        id = id + i,
                        pageURL = "https://pixabay.com/videos/example-" + (id + i),
                        tags = TagForHit?.Invoke(query, i) ?? query,
                        videos = new {
                            medium = new {
                                url = "https://cdn.pixabay.com/video/" + (id + i) + ".mp4",
                                width = 1280,
                                height = 720,
                                size = 8192L
                            }
                        }
                }).ToArray()
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
