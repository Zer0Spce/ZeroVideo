using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// Truthful, highly readable YouTube/TikTok thumbnail hooks derived only
/// from the supplied video headline. Never introduces invented discoveries,
/// suspect identifications, deaths, secrets or false guarantees.
/// </summary>
public static class ThumbnailHeadlineService
{
    private static readonly Regex Words = new(
        @"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GenericOpening = new(
        @"^(?:(?:the|a)\s+)?(?:(?:serial|charming|infamous|notorious)\s+)?"+
        @"(?:killer|ghost hunter|investigator|man|woman|murderer|suspect)\s+who\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> WeakAtBoundary =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "THE","A","AN","AND","OR","OF","FOR","IN","ON",
            "AT","TO","THAT","WHO","IS","WAS","WERE","ARE"
        };

    public static string Hook(string? title,string? topic,int variation=0)
    {
        var raw=string.IsNullOrWhiteSpace(title)?(topic??""):title.Trim();
        raw=Regex.Replace(raw,@"\s*\([^)]{0,40}\)\s*$","").Trim();
        if(raw.Length==0)return "THE UNTOLD STORY";

        // Preserve original wording of the video itself.
        // In "Zak Bagans — The Ghost Hunter Who...", the second half
        // carries the clickable story promise.
        var pieces=Regex.Split(raw,@"\s+[—–-]\s+|:\s+|\s+\|\s+");
        var chosen=pieces.Length>1
            ?pieces.LastOrDefault(p=>!string.IsNullOrWhiteSpace(p))??raw
            :raw;
        chosen=GenericOpening.Replace(chosen.Trim(),"");
        var all=Words.Matches(chosen).Select(m=>m.Value.ToUpperInvariant())
            .Where(w=>w.Length>0).ToList();
        if(all.Count==0) return "THE UNTOLD STORY";

        // Strip empty grammatical edges but never invent extra story facts.
        while(all.Count>2 && WeakAtBoundary.Contains(all[0]))
            all.RemoveAt(0);
        var original=all.ToArray();
        if(all.Count>5)
        {
            var useEnding=variation%2!=0;
            all=useEnding?all.TakeLast(5).ToList():all.Take(5).ToList();
            while(all.Count>2 && WeakAtBoundary.Contains(all[0]))
                all.RemoveAt(0);
            while(all.Count>2 && WeakAtBoundary.Contains(all[^1]))
                all.RemoveAt(all.Count-1);
            if(all.Count<2)
                all=original.TakeLast(Math.Min(5,original.Length)).ToList();
        }
        else if(variation%2!=0 && all.Count>=4 &&
                WeakAtBoundary.Contains(all[0]))
            all.RemoveAt(0);

        // Keep 2–5 meaningful words in one or two uniform lines.
        // For a short famous-name title, show the name instead of a
        // fabricated "shocking" claim.
        if(all.Count==1)
            all=Words.Matches(raw).Select(x=>x.Value.ToUpperInvariant())
                .Take(3).ToList();
        return string.Join(" ",all.Take(5)).Trim();
    }

    public static IReadOnlyList<string> Lines(string text)
    {
        var words=Words.Matches(text).Select(m=>m.Value.ToUpperInvariant())
            .Take(5).ToArray();
        if(words.Length<=2)return [string.Join(" ",words)];
        var candidate=1;
        var score=double.PositiveInfinity;
        for(var i=1;i<words.Length;i++)
        {
            var left=string.Join(" ",words.Take(i));
            var right=string.Join(" ",words.Skip(i));
            var weight=Math.Max(left.Length,right.Length)+
                Math.Abs(left.Length-right.Length)*0.15;
            if(weight<score){score=weight;candidate=i;}
        }
        return [string.Join(" ",words.Take(candidate)),
                string.Join(" ",words.Skip(candidate))];
    }
}
