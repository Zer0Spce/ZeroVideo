namespace ZeroVideo.App.Services;

/// <summary>
/// Applies user-selected, ordered Youtube Flow sources to EACH narration scene.
/// Source order is real: a higher-ranked source gets the scene's slots first.
/// A scene stays independent of its neighbors; failed providers never inject
/// arbitrary media into another scene.
/// </summary>
public static class YouTubeFlowSourceRouter
{
    public const string Pixabay = "pixabay-video";
    public const string CommonsVideo = "wikimedia-video";
    public const string CommonsPhoto = "wikimedia-photo";
    public const string ChatGpt = "chatgpt-image";
    public const string SpongeBob = "spongebob-captures";
    public const string AnimationScreencaps = "animation-screencaps";
    public const string Intanibase = "intanibase";
    public const string Bulbagarden = "bulbagarden";

    public static IReadOnlyList<string> AutomaticSources { get; } =
        [Pixabay, CommonsVideo, CommonsPhoto, ChatGpt];

    public static bool IsReviewOnly(string source)
        => source is SpongeBob or AnimationScreencaps or Intanibase or Bulbagarden;

    public static IReadOnlyList<string> NormalizeOrder(IEnumerable<string> selected)
    {
        var supported = new HashSet<string>(AutomaticSources, StringComparer.Ordinal);
        supported.UnionWith([SpongeBob, AnimationScreencaps, Intanibase, Bulbagarden]);
        return selected.Where(supported.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static int[] SceneQuotas(int sceneCount, int targetClips)
    {
        if (sceneCount <= 0) return [];
        var total = Math.Max(sceneCount, targetClips);
        return Enumerable.Range(0, sceneCount).Select(i =>
            (int)Math.Ceiling((i + 1d) * total / sceneCount) -
            (int)Math.Ceiling(i * total / (double)sceneCount)).ToArray();
    }

    public static async Task<IReadOnlyList<PixabayFlowClip>> AssembleAsync(
        IReadOnlyList<PixabayFootageScene> scenes,
        int targetClips,
        IReadOnlyList<string> enabledPriority,
        Func<IReadOnlyList<int>, CancellationToken, Task<IReadOnlyList<PixabayFlowClip>>> pixabayBatch,
        Func<string, PixabayFootageScene, int, CancellationToken,
            Task<IReadOnlyList<PixabayFlowClip>>> otherSource,
        Func<PixabayFootageScene, CancellationToken, Task<PixabayFlowClip>> sceneCard,
        Action<string>? log,
        CancellationToken ct)
    {
        var order = NormalizeOrder(enabledPriority);
        if (scenes.Count == 0) throw new InvalidDataException("No storyboard scenes to illustrate.");
        if (order.Count == 0)
            throw new InvalidOperationException(
                "Enable at least one media source in YouTube Flow before creating video.");
        var quota = SceneQuotas(scenes.Count, targetClips);
        var items = scenes.Select(_ => new List<PixabayFlowClip>()).ToArray();
        var ids = new HashSet<long>();

        // Never accept media assigned to a different narration scene,
        // regardless of provider or priority. Preserves exact audio order.
        void AddForScene(int index, IEnumerable<PixabayFlowClip> candidates, int max)
        {
            foreach (var clip in candidates)
            {
                if (items[index].Count >= quota[index] || max <= 0) break;
                if (!clip.SearchQuery.Equals(scenes[index].SearchQuery,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(clip.FilePath) || !ids.Add(clip.Id))
                    continue;
                items[index].Add(clip);
                max--;
            }
        }

        for (var p = 0; p < order.Count; p++)
        {
            ct.ThrowIfCancellationRequested();
            var source = order[p];
            var remains = Enumerable.Range(0, scenes.Count)
                .Select(i => Math.Max(0, quota[i] - items[i].Count)).ToArray();
            if (remains.All(x => x == 0)) break;

            if (source == Pixabay)
            {
                // Keep one slot open where practical for enabled later-ranked
                // automatic sources, allowing a real mixed-media documentary
                // without discarding the user's priority order.
                var mixAfter = order.Skip(p + 1).Any(s =>
                    AutomaticSources.Contains(s));
                var budgets = Enumerable.Range(0, scenes.Count)
                    .Select(i => mixAfter && remains[i] >= 2
                        ? remains[i] - 1 : remains[i]).ToArray();
                if (budgets.All(x => x == 0)) continue;
                var stock = await pixabayBatch(budgets, ct);
                for (var i = 0; i < scenes.Count; i++)
                    AddForScene(i, stock.Where(c =>
                        c.MediaSource == "Pixabay"), budgets[i]);
                log?.Invoke($"YouTube Flow: prioritized Pixabay video; " +
                    $"{items.Sum(x => x.Count)} media assets selected.");
                continue;
            }

            for (var i = 0; i < scenes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (items[i].Count >= quota[i]) continue;
                // Avoid repeated generation/downloads: a single illustration
                // or manually reviewed frame can be held for its scene.
                var maximum = source == ChatGpt || IsReviewOnly(source) ? 1
                    : Math.Min(2, quota[i] - items[i].Count);
                IReadOnlyList<PixabayFlowClip> found;
                try
                {
                    found = await otherSource(source, scenes[i], maximum, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.Invoke($"Scene {scenes[i].SceneNumber}: {source} unavailable: " +
                        $"{ex.Message}. Trying the next selected source.");
                    continue;
                }
                AddForScene(i, found ?? [], maximum);
            }
        }

        for (var i = 0; i < scenes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (items[i].Count != 0) continue;
            // A fully local editorial narration card is an explicit,
            // truthful last resort, not a hidden random clip replacement.
            var card = await sceneCard(scenes[i], ct);
            AddForScene(i, [card], 1);
            if (items[i].Count == 0)
                throw new InvalidDataException(
                    $"Unable to cover narration scene {scenes[i].SceneNumber} after all sources and local fallback.");
            log?.Invoke($"Scene {scenes[i].SceneNumber}: all enabled sources unavailable; " +
                "used a labeled narration card instead of unrelated footage.");
        }

        var selected = items.SelectMany(x => x).ToArray();
        log?.Invoke($"YouTube Flow: {selected.Length} topic-matched assets across " +
            $"{scenes.Count} narration scenes, using priority " +
            string.Join(" > ", order) + ".");
        return selected;
    }
}
