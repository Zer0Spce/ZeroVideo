using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Story beats determine location, action and shot scale. A real-person face
/// reference conditions ONLY facial likeness; it must never override the
/// narration or copy the reference headshot composition.
/// </summary>
public static class SceneVisualPlanService
{
    public sealed record ShotPlan(string Name,string Instruction,
        bool Wide,double IdentityMultiplier);

    private static readonly Regex Establishing = new(
        @"\b(wide|full[- ]body|full[- ]length|long[- ]shot|establishing|panoramic|exterior|aerial|street[- ]view|whole (?:room|building))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FacialClose = new(
        @"\b(?:portrait|headshot|face (?:close[- ]up|detail)|(?:tight|extreme) close[- ]up (?:of|on) (?:his|her|the)? ?(?:face|eyes|expression)|facial expression)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Medium = new(
        @"\b(medium[- ]shot|waist[- ]up|chest[- ]up|three[- ]quarter|mid[- ]shot|talking|interview)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ObjectFocus = new(
        @"\b(object close[- ]up|evidence close[- ]up|document close[- ]up|macro|tight (?:shot|view) of (?:a|the) (?:object|evidence|document)|artifact detail)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Activity = new(
        @"\b(walk(?:ed|ing)?|enter(?:ed|ing)?|stand(?:ing)?|open(?:ed|ing)?|investigat(?:ed|ing)|search(?:ed|ing)?|ran|driv(?:ing|e)|arriv(?:ed|ing))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ShotPlan Compose(string? narration,string? direction,
        string? query,long seed,bool identity)
    {
        var visual=(direction??"")+" "+(query??"");
        var action=narration??"";
        var wide=Establishing.IsMatch(visual);
        var face=FacialClose.IsMatch(visual);
        var medium=Medium.IsMatch(visual);
        var detail=ObjectFocus.IsMatch(visual);
        string mode;
        if(wide)mode="wide";
        else if(face)mode="face";
        else if(detail)mode="detail";
        else if(medium)mode="medium";
        else if(Activity.IsMatch(visual))mode="action";
        else
        {
            // Stable variation per scene, not always a repeated headshot;
            // any explicit screenplay framing above takes precedence.
            var variant=(int)((ulong)seed%5);
            mode=variant switch
            {
                0=>"wide",1=>"action",2=>"medium",
                3=>"shoulder",_=>"wide"
            };
        }
        var description=mode switch
        {
            "wide" => "WIDE ESTABLISHING SHOT, show the full location, period-accurate architecture and meaningful action. If the named person is present, include their full body or smaller human figure within the setting; do NOT enlarge the face to dominate the frame.",
            "action" => "MEDIUM-WIDE ACTION SHOT, show the named event actually happening, both environment and character's torso/limbs clearly visible. Compose the action, not a floating head.",
            "medium" => "MEDIUM DOCUMENTARY SHOT, waist-up or three-quarter view with visible environment, objects and interaction that the narration describes. Face recognizable but not filling the frame.",
            "shoulder" => "OVER-THE-SHOULDER DOCUMENTARY VIEW with depth: depict what the person is doing or looking at, keeping the location, clues and hands in frame rather than an isolated headshot.",
            "detail" => "SCENE-SPECIFIC OBJECT OR EVIDENCE DETAIL, focus on the actual named object/action and surroundings. If a person is present, do not replace the important prop with a facial portrait.",
            _ => "FACIAL CLOSE-UP because the scene explicitly requests this framing; retain distinctive facial features and the current scene's true atmosphere."
        };
        if(identity)
            description+=" The supplied reference photo controls ONLY facial identity; never copy the reference photo's gray background, cropping, pose, lighting, clothing or head-and-shoulders composition.";
        var weight=mode switch
        {
            "wide"=>0.68,
            "action"=>0.80,
            "medium"=>0.85,
            "shoulder"=>0.79,
            "detail"=>0.72,
            _=>1.0
        };
        return new ShotPlan(mode,description,
            mode is "wide" or "action",weight);
    }

    private static readonly HashSet<string> Stop = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "the","and","into","from","with","while","that","this","there",
        "when","where","whose","which","what","have","been","were","are",
        "his","her","their","they","them","about","after","before",
        "more","then","only","scene","shot","view","image","video",
        "comic","style","neon","night","dark","dramatic","person",
        "people","story","storytelling","narration","character",
        "illustration","figure","man","woman","said","someone","another",
        "same","background","specific","full","scene", "a","an","to",
        "in","on","of","for","as","at","by","no","not","it","is"
    };
    public static HashSet<string> Keywords(ProjectScene scene)
    {
        var t=string.Join(" ",scene.Narration,scene.ImagePrompt,scene.VisualQuery);
        return Regex.Matches(t.ToLowerInvariant(),@"[\p{L}\p{N}]{3,}")
            .Select(m=>m.Value)
            .Where(x=>!Stop.Contains(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static double Relevance(ProjectScene needed,ProjectScene candidate)
    {
        var ask=Keywords(needed);
        var found=Keywords(candidate);
        if(ask.Count==0 || found.Count==0)return 0;
        var common=ask.Count(x=>found.Contains(x));
        return (double)common/Math.Max(1,Math.Min(ask.Count,found.Count));
    }
}
