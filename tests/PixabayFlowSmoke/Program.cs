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
    await RejectChromaScreenStock();
    await VerifyCommonsLicenseAndImageFallback();
    await VerifyCommonsVideosAndLicenses();
    await VerifyOversizedCommonsDerivativeFallback();
    await VerifyMediumSizedCommonsVideoAllowed();
    VerifyHistoricalVideoContextFiltering();
    await VerifyCommonsVideosInNarrationSequence();
    await VerifyUserSourcePriorityAndDisabledSources();
    await VerifyPixabayNarrationTopicFiltering();
    VerifyRightsReviewedScreencapImport();
    await VerifyChatGptArtworkMixedIntoChronologicalScenes();
    await RescueEmptySceneWithAiImage();
    VerifyTopicSpecificArtPrompts();
    VerifyChatGptGuardrailResponseFallback();
    VerifyActualNarrationCardRender();
    ValidateNarrationTiming();
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
    // New documentary behavior: when EVERY scene is covered, extend the
    // appropriate scene shots instead of forcing 45 separate downloads.
    var stub = new PixabayStub { HitsPerQuery = 5,
        HitsForQuery = q =>
            int.TryParse(q["topic-".Length..], out var i) && i is 0 or 2 or 4 or 6 ? 4 : 3 };
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "too-sparse"), TimeSpan.Zero);
    var scenes = Enumerable.Range(0, 10).Select(i =>
        new PixabayFootageScene(i + 1, "Scene " + i, "topic-" + i, "neutral")).ToArray();
    var matched = await service.AcquireAsync(scenes.Select(x => x.SearchQuery),
        "TEST-KEY", "16:9", null, CancellationToken.None, 45, null, scenes);
    Check(matched.Count == 34 && service.SceneFootageShortfall == 11,
        "All covered narration scenes should render even below previous 85% target.");
    Check(stub.VideoRequests == 34 && scenes.All(scene =>
        matched.Any(c => c.SearchQuery == scene.SearchQuery)),
        "Every narration scene must retain the correct subject's asset.");
    Console.WriteLine("PASS: 34/45 scene-covered video succeeds without unrelated shot repetition");
}

async Task RejectChromaScreenStock()
{
    Check(PixabayVideoService.IsChromaKeyStockMetadata(
        "animated campfire on green screen chroma key"),
        "Stock footage tagged green screen must be blocked.");
    Check(!PixabayVideoService.IsChromaKeyStockMetadata(
        "night forest dark green moss"),
        "Natural greenery must not be excluded from metadata alone.");
    var neonGreen = new byte[144 * 81 * 3];
    for (var i = 0; i < neonGreen.Length; i += 3)
    {
        neonGreen[i] = 2; neonGreen[i + 1] = 246; neonGreen[i + 2] = 10;
    }
    Check(StockVideoQualityService.IsChromaKeyFrame(neonGreen),
        "A flat neon-green video frame must be rejected.");
    var naturalForest = new byte[144 * 81 * 3];
    for (var i = 0; i < naturalForest.Length; i += 3)
    {
        naturalForest[i] = 64; naturalForest[i + 1] = 106;
        naturalForest[i + 2] = 57;
    }
    Check(!StockVideoQualityService.IsChromaKeyFrame(naturalForest),
        "Muted forest greenery should remain usable.");

    var stub = new PixabayStub { HitsPerQuery = 3 };
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "chroma-reject"), TimeSpan.Zero);
    var scenes = new[] {
        new PixabayFootageScene(1, "An ominous spider waits in the forest.",
            "dark forest", "ominous")
    };
    stub.TagForHit = (_, _) => "dark forest night shadows";
    var clips = await service.AcquireAsync(["dark forest"], "TEST-KEY", "16:9",
        null, CancellationToken.None, 2, null, scenes,
        (path, _) => Task.FromResult(Path.GetFileName(path).StartsWith("2100-")));
    Check(clips.Count == 2 && clips.All(x => x.Id != 2100L),
        "Frame-detected chroma video must be replaced by another relevant result.");
    Console.WriteLine("PASS: green-screen and chroma-key stock rejected");
}

async Task VerifyCommonsLicenseAndImageFallback()
{
    Check(WikimediaCommonsImageService.IsCommerciallyReusableLicense(
        "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/", "Photo Author"),
        "CC BY with author and license link is reusable.");
    Check(!WikimediaCommonsImageService.IsMoodCompatible(
        "Australian spider sunny happy picnic", "ominous"),
        "Bright cheerful Commons imagery must not be used for a scary documentary.");
    Check(!WikimediaCommonsImageService.IsMoodCompatible(
        "haunted funeral scene", "upbeat"),
        "Upbeat scenes must reject horrific Commons imagery.");
    Check(WikimediaCommonsImageService.IsMoodCompatible(
        "Australian spider macro photograph", "ominous"),
        "Neutral relevant documentary photographs remain usable with dark grading.");
    Check(!WikimediaCommonsImageService.IsCommerciallyReusableLicense(
        "CC BY-SA 4.0", "https://creativecommons.org/licenses/by-sa/4.0/", "Photo Author"),
        "ShareAlike photos are not eligible for the conservative automatic pipeline.");
    Check(!WikimediaCommonsImageService.IsCommerciallyReusableLicense(
        "CC BY-NC 4.0", "", "Photo Author"),
        "Noncommercial-only photos must not enter monetized documentary videos.");
    Check(WikimediaCommonsImageService.IsCommerciallyReusableLicense(
        "CC0 1.0", "", ""), "Public-domain equivalent CC0 must be eligible.");
    var mock = new CommonsStub();
    using var http = new HttpClient(mock);
    var service = new WikimediaCommonsImageService(
        http, Path.Combine(baseDir, "commons"));
    var scene = new PixabayFootageScene(1,
        "The Australian spider scared parents.",
        "dark Australian spider", "ominous");
    var photos = await service.FindImagesAsync(scene, 2, null, CancellationToken.None);
    Check(photos.Count == 1 && photos[0].IsStillImage && photos[0].Id < 0,
        "Only properly licensed Commons images should be admitted.");
    Check(photos[0].License == "CC BY 4.0" &&
        photos[0].Attribution == "Photo Author" &&
        photos[0].SourcePage.Contains("commons.wikimedia.org"),
        "Attribution and file URL must survive media selection.");
    Check(mock.ApiCalls == 1 && mock.ImageCalls == 1,
        "One Commons API search should download only selected licensed images.");

    var pixabayStub = new PixabayStub { HitsPerQuery = 2 };
    pixabayStub.TagForHit = (_, _) => "dark forest shadows night";
    using var stockClient = new HttpClient(pixabayStub);
    var stock = new PixabayVideoService(stockClient,
        Path.Combine(baseDir, "hybrid"), TimeSpan.Zero);
    var scenes = new[] {
        new PixabayFootageScene(1, "Dark forest at night", "dark forest", "ominous")
    };
    var mixed = await stock.AcquireAsync(["dark forest"], "TEST-KEY", "16:9",
        null, CancellationToken.None, 3, null, scenes, null,
        (_, _, _) => Task.FromResult<IReadOnlyList<PixabayFlowClip>>(photos));
    Check(mixed.Count == 3 && mixed.Count(x => x.IsStillImage) == 1 &&
        mixed.Count(x => !x.IsStillImage) == 2,
        "Two relevant videos plus one Commons photo should fill a 3-shot scene.");
    Console.WriteLine("PASS: licensed Commons photo fallbacks, attribution and mixed scene video");
}

void ValidateNarrationTiming()
{
    var scenes = new[] {
        new PixabayFootageScene(1, "Short intro.", "intro title", "neutral"),
        new PixabayFootageScene(2,
            "A much longer part of the story talks about the televised episode and Australian broadcasters.",
            "television broadcast", "somber")
    };
    var durations = YouTubeFlowRenderService.PlanSceneDurations(scenes, 60d);
    Check(durations.Length == 2 && durations[1] > durations[0] &&
        Math.Abs(durations.Sum() - 60d) < 0.0001,
        "Longer narration segments must receive proportional footage time.");
    Console.WriteLine("PASS: footage timing follows narration length, not arbitrary equal splits");
}

async Task VerifyChatGptArtworkMixedIntoChronologicalScenes()
{
    var stock = new PixabayStub { HitsPerQuery = 3 };
    stock.TagForHit = (query, _) => query == "dark forest"
        ? "dark forest shadows night" : "sunny garden joyful flowers";
    using var http = new HttpClient(stock);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "stock-and-ai"), TimeSpan.Zero);
    var scenes = new[] {
        new PixabayFootageScene(1, "A chilling scene occurs in the forest.",
            "dark forest", "ominous"),
        new PixabayFootageScene(2, "The story ends on a brighter day in the garden.",
            "sunny garden", "upbeat")
    };
    var aiArtwork = new PixabayFlowClip(
        long.MinValue + 2, "scene-02.png",
        "Generated with ChatGPT (not archival footage)",
        "sunny garden", "The story ends on a brighter day in the garden.",
        true, "ChatGPT-generated by ZeroVideo", "AI-generated illustration", "");
    var matched = await service.AcquireAsync(
        scenes.Select(x => x.SearchQuery), "TEST-KEY", "16:9", null,
        CancellationToken.None, 4, null, scenes, null,
        (scene, _, _) => Task.FromResult<IReadOnlyList<PixabayFlowClip>>(
            scene.SceneNumber == 2 ? [aiArtwork] : []));
    Check(matched.Count == 4 && matched.Count(x => x.IsStillImage) == 1 &&
        matched.Count(x => !x.IsStillImage) == 3,
        "The documented mix should include three videos and one AI illustration.");
    Check(matched[3].License == "AI-generated illustration" &&
        matched[3].Id == long.MinValue + 2,
        "AI artwork provenance and distinct ID must survive selection.");
    Check(matched.Take(2).All(c => c.SearchQuery == "dark forest") &&
        matched.Skip(2).All(c => c.SearchQuery == "sunny garden"),
        "AI illustration must stay within its own narration scene.");
    Console.WriteLine("PASS: bounded ChatGPT illustration mixed in scene order and labeled AI-generated");
}

async Task RescueEmptySceneWithAiImage()
{
    var stub = new PixabayStub { HitsPerQuery = 0 };
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "mandatory-ai"), TimeSpan.Zero);
    var scene = new PixabayFootageScene(20,
        "A night-time police confrontation outside a condominium in Florida.",
        "police outside condominium", "ominous");
    var rescue = new PixabayFlowClip(long.MinValue + 20, "scene-20.png",
        "Original AI illustration, not archival footage", scene.SearchQuery,
        scene.NarrationExcerpt, true, "ChatGPT by ZeroVideo",
        "AI-generated illustration", "");
    var callbackCalls = 0;
    var images = await service.AcquireAsync([scene.SearchQuery],
        "TEST-KEY", "16:9", null, CancellationToken.None, 5, null, [scene], null,
        (_, count, _) => {
            callbackCalls++;
            Check(count == -1, "Empty scene must be marked mandatory for AI rescue.");
            return Task.FromResult<IReadOnlyList<PixabayFlowClip>>([rescue]);
        });
    Check(callbackCalls == 1 && images.Count == 1 &&
        images[0].Id == rescue.Id && service.SceneFootageShortfall == 4,
        "One truthful scene-specific image should rescue five shot slots.");
    Console.WriteLine("PASS: zero-coverage scene rescued with original image instead of hard stop");
}

async Task VerifyUserSourcePriorityAndDisabledSources()
{
    var scene = new PixabayFootageScene(1,
        "A dark spider crawls beside a television broadcast.",
        "dark spider television", "ominous");
    var pix = new[]
    {
        new PixabayFlowClip(5001, "test-pix-1.mp4", "https://pixabay.com/",
            scene.SearchQuery, "dark spider television"),
        new PixabayFlowClip(5002, "test-pix-2.mp4", "https://pixabay.com/",
            scene.SearchQuery, "dark spider television"),
        new PixabayFlowClip(5003, "test-pix-3.mp4", "https://pixabay.com/",
            scene.SearchQuery, "dark spider television")
    };
    var commons = new PixabayFlowClip(-6001, "test-commons.webm",
        "https://commons.wikimedia.org/wiki/File:Spider.webm", scene.SearchQuery,
        "dark spider television", false, "Creator", "CC BY 4.0",
        "https://creativecommons.org/licenses/by/4.0/", "Wikimedia Commons");
    var pixCalls = 0;
    async Task<IReadOnlyList<PixabayFlowClip>> PixabayBatch(
        IReadOnlyList<int> budgets, CancellationToken ct)
    {
        pixCalls++;
        return await Task.FromResult(pix.Take(budgets[0]).ToArray());
    }
    Task<IReadOnlyList<PixabayFlowClip>> Other(
        string source, PixabayFootageScene _, int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PixabayFlowClip>>(
            source == YouTubeFlowSourceRouter.CommonsVideo ? [commons] : []);
    Task<PixabayFlowClip> Card(PixabayFootageScene _, CancellationToken ct)
        => Task.FromResult(new PixabayFlowClip(-100000,
            "scene-card.png", "Original narration", scene.SearchQuery,
            scene.NarrationExcerpt, true));

    var pixFirst = await YouTubeFlowSourceRouter.AssembleAsync(
        [scene], 3, [YouTubeFlowSourceRouter.Pixabay,
            YouTubeFlowSourceRouter.CommonsVideo],
        PixabayBatch, Other, Card, null, CancellationToken.None);
    Check(pixFirst.Count == 3 && pixFirst[0].MediaSource == "Pixabay" &&
        pixFirst[1].MediaSource == "Pixabay" &&
        pixFirst[2].MediaSource == "Wikimedia Commons",
        "Pixabay first must take its priority slots before Wikimedia video.");
    var commonsFirst = await YouTubeFlowSourceRouter.AssembleAsync(
        [scene], 3, [YouTubeFlowSourceRouter.CommonsVideo,
            YouTubeFlowSourceRouter.Pixabay],
        PixabayBatch, Other, Card, null, CancellationToken.None);
    Check(commonsFirst.Count == 3 &&
        commonsFirst[0].MediaSource == "Wikimedia Commons" &&
        commonsFirst[1].MediaSource == "Pixabay",
        "Moving Wikimedia Up must change the actual per-scene media order.");
    var oldPixCalls = pixCalls;
    var wikimediaOnly = await YouTubeFlowSourceRouter.AssembleAsync(
        [scene], 3, [YouTubeFlowSourceRouter.CommonsVideo],
        PixabayBatch, Other, Card, null, CancellationToken.None);
    Check(pixCalls == oldPixCalls &&
        wikimediaOnly.Count == 1 &&
        wikimediaOnly[0].MediaSource == "Wikimedia Commons",
        "Unchecking Pixabay must avoid all Pixabay API/download calls.");
    var noSource = await YouTubeFlowSourceRouter.AssembleAsync(
        [scene], 3, [YouTubeFlowSourceRouter.ChatGpt],
        PixabayBatch, Other, Card, null, CancellationToken.None);
    Check(noSource.Count == 1 && noSource[0].IsStillImage,
        "Empty enabled source must fall back to a truthful local narration card.");
    Console.WriteLine("PASS: checkboxes and Up/Down source priority change actual video source order");
}

async Task VerifyPixabayNarrationTopicFiltering()
{
    var onTopic = new PixabayFootageScene(1,
        "The episode showed a spider crawling across a dark television screen.",
        "dark spider television", "ominous");
    Check(PixabayTopicRelevanceService.IsRelevant(
        onTopic, "The Dark Side of Cartoons",
        "dark spider television eerie shadows", out _),
        "Stock tags must actually depict concrete items in the narration.");
    var offTopic = new PixabayFootageScene(1,
        "The broadcast network discussed a children's animated episode.",
        "dark squirrel forest", "ominous");
    Check(!PixabayTopicRelevanceService.IsRelevant(
        offTopic, "The Dark Side of Cartoons",
        "dark squirrel forest night", out var reason) && reason.Contains("narration"),
        "A generic query can match Pixabay tags but must fail narration relevance.");
    var filler = new PixabayFootageScene(1,
        "Parents were worried about the television broadcast.",
        "parents television discussion", "tense");
    Check(!PixabayTopicRelevanceService.IsRelevant(
        filler, "Peppa Pig episode controversy",
        "parents television discussion nutella eating", out _),
        "Cartoon documentary cannot insert unrelated food/animals as filler.");
    var stub = new PixabayStub { HitsPerQuery = 3 };
    stub.TagForHit = (_, _) => "dark squirrel forest night";
    using var http = new HttpClient(stub);
    var service = new PixabayVideoService(http,
        Path.Combine(baseDir, "topic-gate"), TimeSpan.Zero);
    var chosen = await service.AcquireAsync([offTopic.SearchQuery],
        "TEST-KEY", "16:9", null, CancellationToken.None, 3, null, [offTopic],
        null, null, null, [3], true, "The Dark Side of Cartoons");
    Check(chosen.Count == 0 && stub.VideoRequests == 0,
        "No unrelated Pixar/Pixabay downloads when the query is unrelated to narration.");
    Console.WriteLine("PASS: Pixabay actual narration/topic relevance gate rejects generic and random clips");
}

void VerifyRightsReviewedScreencapImport()
{
    var folder = Path.Combine(baseDir, "approved-gallery-test");
    var media = Path.Combine(folder, "approved-screencaps");
    Directory.CreateDirectory(media);
    var scene = new PixabayFootageScene(9,
        "An episode of the animated show is discussed.",
        "cartoon episode television", "neutral");
    var source = YouTubeFlowSourceRouter.SpongeBob;
    var stem = "scene-09-spongebob-captures";
    File.WriteAllBytes(Path.Combine(media, stem + ".png"), new byte[8192]);
    Check(YouTubeFlowScreencapReviewService.FindApprovedImages(
        folder, scene, source).Count == 0,
        "Cartoon screenshot must NOT load merely because the user toggled a gallery.");
    File.WriteAllText(Path.Combine(media, stem + ".json"),
        JsonSerializer.Serialize(new {
            approvedForUse = false,
            sourceUrl = "https://thesbcommunity.com/captures/episode",
            usageBasis = "User has reviewed permission or fair-use needs",
            credit = "Original rights holder"
        }));
    Check(YouTubeFlowScreencapReviewService.FindApprovedImages(
        folder, scene, source).Count == 0,
        "User must explicitly approve screenshot use before import.");
    File.WriteAllText(Path.Combine(media, stem + ".json"),
        JsonSerializer.Serialize(new {
            approvedForUse = true,
            sourceUrl = "https://thesbcommunity.com/captures/episode",
            usageBasis = "User has reviewed permission or fair-use needs",
            credit = "Original rights holder"
        }));
    var approved = YouTubeFlowScreencapReviewService.FindApprovedImages(
        folder, scene, source);
    Check(approved.Count == 1 && approved[0].MediaSource == source &&
        approved[0].IsStillImage &&
        approved[0].SourcePage.Contains("thesbcommunity.com"),
        "Reviewed copyright screenshot may enter only its selected scene with credits.");
    Console.WriteLine("PASS: copyright-screened cartoon screencaps require explicit reviewed imports");
}

void VerifyTopicSpecificArtPrompts()
{
    var scene = new PixabayFootageScene(1,
        "Peppa Pig introduced a spider in a children's television episode.",
        "cartoon spider episode", "ominous");
    var prompt = YouTubeFlowVisualPromptService.BuildScenePrompt(
        "The Dark Side of Peppa Pig", scene, "16:9");
    Check(!prompt.Contains("Peppa Pig", StringComparison.OrdinalIgnoreCase) &&
        !prompt.Contains("piglet", StringComparison.OrdinalIgnoreCase) &&
        prompt.Contains("spider", StringComparison.OrdinalIgnoreCase) &&
        prompt.Contains("No franchise characters", StringComparison.OrdinalIgnoreCase),
        "Franchise scenes must request contextual art, not recognizable character likenesses.");
    var historical = YouTubeFlowVisualPromptService.BuildScenePrompt(
        "Disney's Town of Celebration", new PixabayFootageScene(
            28, "Neighborhood homes and privately governed property arrangements.",
            "suburban community agreements", "ominous"), "16:9");
    Check(historical.Contains("Celebration, Florida") &&
        historical.Contains("futuristic cities"),
        "Real-location scenes must avoid invented futuristic infrastructure.");
    var thumb = YouTubeFlowVisualPromptService.BuildThumbnailPrompt(
        "The Dark Side of Peppa Pig", scene.NarrationExcerpt,
        "Creepy Cartoon Commentary", "TOO SCARY?");
    Check(!thumb.Contains("Peppa Pig", StringComparison.OrdinalIgnoreCase) &&
        !thumb.Contains("piglet", StringComparison.OrdinalIgnoreCase) &&
        thumb.Contains("TOO SCARY?") && thumb.Contains("spider"),
        "A franchise thumbnail must keep scene context without copyrighted character prompts.");
    Check(ChatGptComposer.IsImageGenerationPrompt(prompt) &&
        ChatGptComposer.IsImageGenerationPrompt(thumb),
        "Both illustration and thumbnail prompts must enter ChatGPT image mode.");
    Console.WriteLine("PASS: copyright-safe contextual visuals, correct image routing and truthful subject prompts");
}

void VerifyChatGptGuardrailResponseFallback()
{
    var refusal = "We’re so sorry, but the image we created may violate our " +
        "guardrails concerning similarity to third-party content. Please retry or edit your prompt.";
    Check(ChatGptImageResponseGuard.IsRefusal(refusal),
        "Real ChatGPT third-party similarity refusal must be detected immediately.");
    Check(ChatGptImageResponseGuard.IsRefusal(
        "I cannot generate this image due to image policy restrictions."),
        "Explicit image-generation denial must enter fallback.");
    Check(!ChatGptImageResponseGuard.IsRefusal(
        "The documentary scene illustrates a spider and children's TV controversy."),
        "Normal image-related replies must not be marked refused.");
    Check(!ChatGptImageResponseGuard.IsRefusal(
        "Do not copy third-party cartoon art or official character screenshots."),
        "User prompt rules must not falsely trigger guardrail detection.");
    Console.WriteLine("PASS: image guardrail refusal recognized and normal scene text preserved");
}

async Task VerifyCommonsVideosAndLicenses()
{
    var fake = new CommonsVideoStub();
    using var client = new HttpClient(fake);
    var dir = Path.Combine(baseDir, "commons-video");
    var service = new WikimediaCommonsVideoService(client, dir);
    var scene = new PixabayFootageScene(1,
        "An eerie, shadowy forest scene at night.",
        "dark forest night", "ominous");
    var first = await service.FindVideosAsync(scene, 2, null, null, CancellationToken.None);
    Check(first.Count == 1 && first[0].MediaSource == "Wikimedia Commons" &&
        !first[0].IsStillImage && first[0].License == "CC BY 4.0" &&
        first[0].Attribution == "Video Author" &&
        first[0].FilePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase),
        "Wikimedia video must retain valid CC BY license, attribution and WebM media kind.");
    Check(first[0].SourcePage.StartsWith("https://commons.wikimedia.org/wiki/") &&
        File.Exists(first[0].FilePath), "Accepted Commons video needs a source page and local file.");
    Check(fake.ApiCalls == 1 && fake.VideoCalls == 1,
        "Only the selected eligible video may be downloaded.");
    var cached = new WikimediaCommonsVideoService(client, dir);
    var second = await cached.FindVideosAsync(scene, 1, null, null, CancellationToken.None);
    Check(second.Count == 1 && fake.ApiCalls == 1 && fake.VideoCalls == 1,
        "Fresh Commons API results and media files must be reused for this project.");
    Check(!WikimediaCommonsVideoService.IsAllowedVideoUrl(
        "https://evil.example/video.webm") &&
        WikimediaCommonsVideoService.IsAllowedVideoUrl(
        "https://upload.wikimedia.org/wikipedia/commons/example.webm"),
        "Only trusted HTTPS Wikimedia hosts may serve video files.");
    Console.WriteLine("PASS: Commons videos, licenses, source provenance, caching and safe downloads");
}

async Task VerifyOversizedCommonsDerivativeFallback()
{
    // The 500 MB hard cap remains a safety limit; a 600 MB transcode must
    // fall through to a smaller eligible rendition instead of aborting.
    var fake = new CommonsVideoStub { OversizedFirstDerivative = true };
    using var client = new HttpClient(fake);
    var service = new WikimediaCommonsVideoService(client,
        Path.Combine(baseDir, "commons-over-80mb"));
    var scene = new PixabayFootageScene(1,
        "A dark forest stands silent through the night.",
        "dark forest night", "ominous");
    var log = new List<string>();
    var matched = await service.FindVideosAsync(
        scene, 1, null, log.Add, CancellationToken.None);
    Check(matched.Count == 1 && File.Exists(matched[0].FilePath) &&
        new FileInfo(matched[0].FilePath).Length <=
            WikimediaCommonsVideoService.MaxVideoMegabytes * 1024L * 1024,
        "A 600 MB 480p transcode must not prevent selecting a smaller file.");
    Check(fake.VideoCalls == 2 && matched[0].FilePath.EndsWith(".webm") &&
        log.Any(x => x.Contains("Wikimedia video: scene 1", StringComparison.Ordinal)),
        "Service must skip >500MB file from headers before download and try smaller WebM.");
    Console.WriteLine("PASS: >500MB Wikimedia video falls back to smaller WebM transcode");
}

async Task VerifyMediumSizedCommonsVideoAllowed()
{
    // Real user issue: Wikimedia videos between 80 and 500 MB must now be
    // eligible, rather than automatically rejected as oversized.
    Check(WikimediaCommonsVideoService.MaxVideoMegabytes == 500,
        "Commons video download limit must be 500 MB.");
    var fake = new CommonsVideoStub { MediumFirstDerivative = true };
    using var client = new HttpClient(fake);
    var service = new WikimediaCommonsVideoService(client,
        Path.Combine(baseDir, "commons-96mb"));
    var scene = new PixabayFootageScene(1,
        "A dark forest stands silent through the night.",
        "dark forest night", "ominous");
    var matched = await service.FindVideosAsync(
        scene, 1, null, null, CancellationToken.None);
    Check(matched.Count == 1 && fake.VideoCalls == 1 &&
        matched[0].FilePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase),
        "A 96 MB Content-Length from Commons must be allowed as a valid video.");
    Console.WriteLine("PASS: Wikimedia video advertised at 96MB is accepted under 500MB limit");
}

void VerifyHistoricalVideoContextFiltering()
{
    var scene = new PixabayFootageScene(11,
        "A town's house committee considered changes to resident regulations.",
        "house committee", "neutral");
    Check(!WikimediaCommonsVideoService.IsRelevantToNarration(
        scene, "File:William Weld Discusses the House Committee on the Judiciary Impeachment Inquiry.webm",
        "A long historical interview with former governor William Weld."),
        "Do not pretend an unrelated named historical interview illustrates town rules.");
    Check(!WikimediaCommonsVideoService.IsRelevantToNarration(
        scene, "File:G. Gordon Liddy Recalls How the Watergate Burglars Were Caught.webm",
        "Watergate political interview about burglars"),
        "Unrelated politicians and historical speakers may not fill a neighborhood governance scene.");
    Check(WikimediaCommonsVideoService.IsRelevantToNarration(
        new PixabayFootageScene(11,
            "William Weld discusses the House Committee during an impeachment inquiry.",
            "house committee impeachment", "neutral"),
        "File:William Weld Discusses the House Committee on the Judiciary Impeachment Inquiry.webm",
        "Interview with William Weld about impeachment inquiry"),
        "Explicitly narrated historical speakers must remain eligible.");
    Check(!WikimediaCommonsVideoService.IsRelevantToNarration(
        new PixabayFootageScene(9,
            "Residents investigated a community's government and daily life.",
            "town government records", "neutral"),
        "File:Man Belongs to the Earth opening.webm",
        "1960s experimental documentary about earth and humanity"),
        "A keyword-only broad match should not pass when the narration contains no matching subject.");
    Console.WriteLine("PASS: historical speeches and unrelated movies excluded unless actually narrated");
}

async Task VerifyCommonsVideosInNarrationSequence()
{
    var fake = new CommonsVideoStub();
    using var mediaHttp = new HttpClient(fake);
    var service = new WikimediaCommonsVideoService(
        mediaHttp, Path.Combine(baseDir, "commons-into-storyboard"));
    var pixabayStub = new PixabayStub { HitsPerQuery = 0 };
    using var pixabayHttp = new HttpClient(pixabayStub);
    var pixabay = new PixabayVideoService(
        pixabayHttp, Path.Combine(baseDir, "commons-storyboard"), TimeSpan.Zero);
    var scene = new PixabayFootageScene(1,
        "The shadowy forest at night.",
        "dark forest night", "ominous");
    var matched = await pixabay.AcquireAsync(
        [scene.SearchQuery], "TEST-KEY", "16:9", null,
        CancellationToken.None, 4, null, [scene],
        null, null,
        (x, count, ct) => service.FindVideosAsync(x, count, null, null, ct));
    Check(matched.Count == 1 && matched[0].MediaSource == "Wikimedia Commons" &&
        matched[0].SearchQuery == scene.SearchQuery &&
        pixabay.SceneFootageShortfall == 3,
        "An actual scene may be rescued by Commons video without a Pixabay result or unrelated image.");
    Console.WriteLine("PASS: Commons video rescues an empty storyboard scene without breaking narration");
}

void VerifyActualNarrationCardRender()
{
    // The final fallback must produce a real PNG even if every remote
    // provider is unavailable. WPF image rendering requires an STA thread.
    var scene = new PixabayFootageScene(29,
        "Residents continued living, discussing shared decisions, and building community.",
        "neighbors discussing town rules", "calm");
    Exception? failure = null;
    string? path = null;
    var thread = new Thread(() =>
    {
        try
        {
            path = SceneFactCardService.Create(scene,
                Path.Combine(baseDir, "real-narration-card"));
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(25)))
        throw new Exception("Rendering the last-resort narration card timed out.");
    if (failure is not null)
        throw new Exception("WPF failed to generate a last-resort narration card.", failure);
    Check(path is not null && File.Exists(path) &&
        new FileInfo(path).Length > 2000,
        "A real fallback PNG must be created and available for FFmpeg.");
    Console.WriteLine("PASS: real 1920x1080 narration-based fallback PNG renders locally");
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


sealed class CommonsStub : HttpMessageHandler
{
    public int ApiCalls { get; private set; }
    public int ImageCalls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/w/api.php", StringComparison.Ordinal))
        {
            ApiCalls++;
            var json = JsonSerializer.Serialize(new
            {
                query = new {
                    pages = new object[] {
                        new {
                            pageid = 9001,
                            title = "File:Australian spider macro photograph.jpg",
                            imageinfo = new[] { new {
                                mime = "image/jpeg",
                                thumburl = "https://upload.wikimedia.org/wikipedia/commons/thumb/a/spider.jpg",
                                extmetadata = new {
                                    LicenseShortName = new { value = "CC BY 4.0" },
                                    LicenseUrl = new { value = "https://creativecommons.org/licenses/by/4.0/" },
                                    Artist = new { value = "<b>Photo Author</b>" },
                                    ImageDescription = new { value = "Australian spider macro image" },
                                    Restrictions = new { value = "" }
                                }
                            }}
                        },
                        new {
                            pageid = 9003,
                            title = "File:Australian national library building.jpg",
                            imageinfo = new[] { new {
                                mime = "image/jpeg",
                                thumburl = "https://upload.wikimedia.org/wikipedia/commons/thumb/c/library.jpg",
                                extmetadata = new {
                                    LicenseShortName = new { value = "CC BY 4.0" },
                                    LicenseUrl = new { value = "https://creativecommons.org/licenses/by/4.0/" },
                                    Artist = new { value = "Unrelated Author" },
                                    ImageDescription = new { value = "Australian library architecture" },
                                    Restrictions = new { value = "" }
                                }
                            }}
                        },
                        new {
                            pageid = 9002,
                            title = "File:Australian spider noncommercial.jpg",
                            imageinfo = new[] { new {
                                mime = "image/jpeg",
                                thumburl = "https://upload.wikimedia.org/wikipedia/commons/thumb/b/spider.jpg",
                                extmetadata = new {
                                    LicenseShortName = new { value = "CC BY-NC 4.0" },
                                    LicenseUrl = new { value = "https://creativecommons.org/licenses/by-nc/4.0/" },
                                    Artist = new { value = "<b>Restricted Author</b>" },
                                    ImageDescription = new { value = "Australian spider" },
                                    Restrictions = new { value = "" }
                                }
                            }}
                        }
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            ImageCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[8192])
            });
        }
        throw new Exception("Unexpected Commons request: " + request.RequestUri);
    }
}


sealed class CommonsVideoStub : HttpMessageHandler
{
    public int ApiCalls { get; private set; }
    public int VideoCalls { get; private set; }
    public bool OversizedFirstDerivative { get; init; }
    public bool MediumFirstDerivative { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? "";
        if (url.Contains("/w/api.php?", StringComparison.OrdinalIgnoreCase))
        {
            ApiCalls++;
            var json = JsonSerializer.Serialize(new
            {
                query = new
                {
                    pages = new object[] {
                        new {
                            pageid = 101,
                            title = "File:Dark forest night shadows.webm",
                            videoinfo = new[] { new {
                                mime = "video/webm", size = 12000000,
                                url = "https://upload.wikimedia.org/wikipedia/commons/a/source.webm",
                                derivatives = OversizedFirstDerivative
                                    ? new [] {
                                        new {
                                            src = "https://upload.wikimedia.org/wikipedia/commons/transcoded/a/forest.webm",
                                            type = "video/webm", width = 854, height = 480
                                        },
                                        new {
                                            src = "https://upload.wikimedia.org/wikipedia/commons/transcoded/a/forest-small.webm",
                                            type = "video/webm", width = 640, height = 360
                                        }
                                      }
                                    : new [] { new {
                                        src = "https://upload.wikimedia.org/wikipedia/commons/transcoded/a/forest.webm",
                                        type = "video/webm", width = 854, height = 480
                                    } },
                                extmetadata = new {
                                    LicenseShortName = new { value = "CC BY 4.0" },
                                    LicenseUrl = new { value = "https://creativecommons.org/licenses/by/4.0/" },
                                    Artist = new { value = "<b>Video Author</b>" },
                                    ImageDescription = new { value = "Dark forest night shadows under the moon" },
                                    Restrictions = new { value = "" }
                                }
                            }}
                        },
                        new {
                            pageid = 102,
                            title = "File:Dark forest night unlicensed.webm",
                            videoinfo = new[] { new {
                                mime = "video/webm", size = 5000000,
                                url = "https://upload.wikimedia.org/wikipedia/commons/b/forbidden.webm",
                                derivatives = Array.Empty<object>(),
                                extmetadata = new {
                                    LicenseShortName = new { value = "CC BY-SA 4.0" },
                                    LicenseUrl = new { value = "https://creativecommons.org/licenses/by-sa/4.0/" },
                                    Artist = new { value = "ShareAlike Author" },
                                    ImageDescription = new { value = "Dark forest night" },
                                    Restrictions = new { value = "" }
                                }
                            }}
                        },
                        new {
                            pageid = 103,
                            title = "File:Happy sunny forest vacation.webm",
                            videoinfo = new[] { new {
                                mime = "video/webm", size = 5000000,
                                url = "https://upload.wikimedia.org/wikipedia/commons/c/irrelevant.webm",
                                derivatives = Array.Empty<object>(),
                                extmetadata = new {
                                    LicenseShortName = new { value = "CC0 1.0" },
                                    LicenseUrl = new { value = "" },
                                    Artist = new { value = "" },
                                    ImageDescription = new { value = "Sunny happy vacation in the forest" },
                                    Restrictions = new { value = "" }
                                }
                            }}
                        }
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
        {
            VideoCalls++;
            var body = new ByteArrayContent(new byte[8192]);
            if (url.EndsWith("/forest.webm", StringComparison.OrdinalIgnoreCase))
            {
                if (OversizedFirstDerivative)
                    body.Headers.ContentLength = 600L * 1024 * 1024;
                else if (MediumFirstDerivative)
                    body.Headers.ContentLength = 96L * 1024 * 1024;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = body
            });
        }
        throw new Exception("Unexpected Commons video URL: " + request.RequestUri);
    }
}
