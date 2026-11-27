using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// A shared, deterministic, narration-anchored timeline for visual scene
/// changes AND burned captions / SRT output. Uses speech/pause detection from
/// the actual chapter audio; when no reliable pauses exist, distributes time
/// by spoken-word weights (rather than stale outline targetSeconds).
/// This is not word-level ASR/forced phoneme alignment.
/// </summary>
public static class NarrationTimelineService
{
    public readonly record struct Beat(double Start, double End)
    {
        public double Duration => Math.Max(0,End-Start);
    }

    public sealed record ChapterTiming(double Duration, IReadOnlyList<Beat> Scenes,
        IReadOnlyList<Beat> Speech);

    private static readonly Regex Words = new(
        @"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*[.!?,;:]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static List<string> SplitWords(string? script) =>
        Words.Matches(script??"").Select(x=>x.Value).ToList();

    public static double WordWeight(string word)
    {
        var letters=Regex.Replace(word,@"[^\p{L}\p{N}]","");
        return Math.Clamp(letters.Length/4.2,0.85,3.4);
    }

    private static double ScriptWeight(string script) =>
        Math.Max(1,SplitWords(script).Sum(WordWeight));

    public static bool ScriptsMatch(ProjectChapter chapter)
    {
        if (string.IsNullOrWhiteSpace(chapter.Narration)) return true;
        var sceneNarration=string.Join(" ",chapter.Scenes.Select(s=>s.Narration));
        static string Normalize(string x) => string.Join(" ",
            SplitWords(x).Select(w=>Regex.Replace(w.ToUpperInvariant(),
                @"[^\p{L}\p{N}]","")));
        return string.Equals(Normalize(chapter.Narration),
            Normalize(sceneNarration),StringComparison.Ordinal);
    }

    /// <summary>
    /// The chapter-level TTS uses chapter.Narration, so captions must use
    /// EXACTLY that text even in an older saved project where scene.Narration
    /// differs. Distribute its tokens across scenes by the original scene
    /// word counts; never splice neighboring scene text into a caption.
    /// </summary>
    public static List<List<string>> SpokenSceneWords(ProjectChapter chapter)
    {
        var perScene=chapter.Scenes.Select(s=>SplitWords(s.Narration)).ToList();
        if (ScriptsMatch(chapter) || string.IsNullOrWhiteSpace(chapter.Narration))
            return perScene;
        var source=SplitWords(chapter.Narration);
        var weights=perScene.Select(s=>Math.Max(1,s.Sum(WordWeight))).ToArray();
        var total=weights.Sum();
        var result=new List<List<string>>();
        var last=0;
        var cumulative=0.0;
        for(var i=0;i<weights.Length;i++)
        {
            cumulative+=weights[i];
            var target=i==weights.Length-1 ? source.Count :
                (int)Math.Round(source.Count*cumulative/total);
            target=Math.Clamp(target,last,source.Count);
            result.Add(source.Skip(last).Take(target-last).ToList());
            last=target;
        }
        return result;
    }

    private static IReadOnlyList<Beat> SanitizeSpeech(
        IReadOnlyList<(double Start,double End)> raw,double duration)
    {
        var ordered=raw.Select(s=>(Start:Math.Clamp(s.Start,0,duration),
                End:Math.Clamp(s.End,0,duration)))
            .Where(s=>s.End-s.Start>0.04).OrderBy(s=>s.Start);
        var merged=new List<Beat>();
        foreach(var s in ordered)
        {
            if(merged.Count>0 && s.Start<=merged[^1].End)
                merged[^1]=new Beat(merged[^1].Start,
                    Math.Max(merged[^1].End,s.End));
            else
                merged.Add(new Beat(s.Start,s.End));
        }
        return merged.Count>0?merged:[new Beat(0,duration)];
    }

    public static double SpeechPosition(
        IReadOnlyList<Beat> speech,double progress)
    {
        if(speech.Count==0) return 0;
        var total=speech.Sum(x=>x.Duration);
        if(total<=0) return speech[0].Start;
        var remaining=Math.Clamp(progress,0,1)*total;
        foreach(var span in speech)
        {
            if(remaining<=span.Duration) return span.Start+remaining;
            remaining-=span.Duration;
        }
        return speech[^1].End;
    }

    public static double SnapNearbyPause(
        double requested,IReadOnlyList<Beat> speech,double within=0.35)
    {
        var best=requested;
        var distance=within;
        for(var i=1;i<speech.Count;i++)
        {
            var pause=speech[i].Start-speech[i-1].End;
            if(pause<0.12 || pause>1.5) continue;
            var middle=(speech[i].Start+speech[i-1].End)/2;
            var gap=Math.Abs(middle-requested);
            if(gap<distance) { best=middle; distance=gap; }
        }
        return best;
    }

    public static ChapterTiming Plan(ProjectChapter chapter,double duration,
        IReadOnlyList<(double Start,double End)> speechWindows)
    {
        duration=Math.Max(0.1,duration);
        var active=SanitizeSpeech(speechWindows,duration);
        var count=chapter.Scenes.Count;
        if(count==0) return new ChapterTiming(duration,[],active);
        var text=SpokenSceneWords(chapter);
        var words=text.Select(s=>Math.Max(1,s.Sum(WordWeight))).ToArray();
        // A small share of the author-specified scene weight keeps visuals
        // from changing too abruptly when scenes have very few spoken words.
        var totalWords=words.Sum();
        var desired=chapter.Scenes.Select(s=>Math.Max(0.25,s.TargetSeconds)).ToArray();
        var plannedTotal=desired.Sum();
        var weights=words.Select((w,i)=>
            0.90*w/totalWords+0.10*desired[i]/plannedTotal).ToArray();
        var weightTotal=weights.Sum();
        var cumulative=0.0;
        var boundaries=new double[count+1];
        boundaries[0]=0;
        boundaries[^1]=duration;
        var min=Math.Min(0.25,duration/(2*count));
        for(var i=1;i<count;i++)
        {
            cumulative+=weights[i-1];
            var raw=SpeechPosition(active,cumulative/weightTotal);
            var target=SnapNearbyPause(raw,active);
            boundaries[i]=Math.Clamp(target,
                boundaries[i-1]+min,duration-(count-i)*min);
        }
        var beats=new List<Beat>(count);
        for(var i=0;i<count;i++)
            beats.Add(new Beat(boundaries[i],boundaries[i+1]));
        return new ChapterTiming(duration,beats,active);
    }

    public static IReadOnlyList<Beat> SpeechInBeat(
        ChapterTiming timing, Beat beat)
    {
        var clipped=timing.Speech.Select(s=>
                new Beat(Math.Max(s.Start,beat.Start),
                    Math.Min(s.End,beat.End)))
            .Where(s=>s.Duration>0.025).ToList();
        return clipped.Count>0?clipped:[beat];
    }

    public static List<List<string>> GroupCaptionWords(
        IReadOnlyList<string> words)
    {
        var groups=new List<List<string>>();
        var current=new List<string>();
        foreach(var word in words)
        {
            var proposed=current.Sum(x=>x.Length)+current.Count+word.Length;
            if(current.Count>0&&(current.Count>=3 || proposed>17))
            {
                groups.Add(current);
                current=[];
            }
            current.Add(word);
            if(Regex.IsMatch(word,@"[.!?;:]$"))
            {
                groups.Add(current);
                current=[];
            }
        }
        if(current.Count>0)groups.Add(current);
        return groups;
    }

    public static List<(Beat Time,List<string> Words)> CaptionCues(
        ProjectChapter chapter,ChapterTiming timing)
    {
        var scenes=SpokenSceneWords(chapter);
        var cues=new List<(Beat,List<string>)>();
        var last=0.0;
        for(var i=0;i<timing.Scenes.Count;i++)
        {
            var scene=timing.Scenes[i];
            var groups=GroupCaptionWords(scenes[i]);
            if(groups.Count==0) continue;
            var total=groups.Sum(g=>g.Sum(WordWeight));
            var speech=SpeechInBeat(timing,scene);
            var completed=0.0;
            foreach(var group in groups)
            {
                var from=SpeechPosition(speech,completed/total);
                completed+=group.Sum(WordWeight);
                var to=SpeechPosition(speech,completed/total);
                // Never spill text into an adjacent scene or overlap the
                // preceding caption after centisecond ASS rounding.
                from=Math.Max(Math.Max(scene.Start,last),from);
                to=Math.Min(scene.End,to);
                if(to-from<0.11)
                {
                    to=Math.Min(scene.End,from+0.11);
                    if(to-from<0.09)continue;
                }
                cues.Add((new Beat(from,to),group));
                last=to;
            }
        }
        return cues;
    }
}
