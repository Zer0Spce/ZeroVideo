using System.Text;

namespace ZeroVideo.App.Services;

/// <summary>
/// Conservative ASS caption layout for a 1080×1920 reference frame. We set
/// both horizontal margins and explicit q2/no-automatic-wrap so libass does
/// not unexpectedly create a third line or clip words off the frame.
/// </summary>
public static class CaptionLayoutService
{
    public const int CaptionMargin = 145;
    public const int MaximumFontSize = 76;
    public const int MinimumFontSize = 42;
    // Deliberately narrower than 1080-2*Margin; leaves room for outline.
    public const int TextSafeWidth = 720;
    public const int LandscapeTextSafeWidth = 1320;
    public const int LandscapeMaxFontSize = 68;
    public const int LandscapeMinFontSize = 44;

    private static double VisualUnits(string phrase)
    {
        double units = 0;
        foreach (var c in phrase.ToUpperInvariant())
        {
            if (c == ' ') units += 0.40;
            else if ("MW@%".Contains(c)) units += 1.15;
            else if ("OQGHN".Contains(c)) units += 0.94;
            else if ("IJL".Contains(c)) units += 0.68;
            else if (".,!:;'’".Contains(c)) units += 0.31;
            else if ("()-".Contains(c)) units += 0.57;
            else units += 0.86;
        }
        return Math.Max(1, units);
    }

    private static string EscapeWord(string value)
    {
        // Text is always white and only renderer-controlled ASS overrides
        // are allowed; never preserve raw user-authored formatting escapes.
        var upper=value.ToUpperInvariant();
        return upper.Replace("\\","").Replace("{","").Replace("}","");
    }

    private static (int FontSize,int ScaleX) Fit(double units,bool landscape)
    {
        var safe=landscape?LandscapeTextSafeWidth:TextSafeWidth;
        var min=landscape?LandscapeMinFontSize:MinimumFontSize;
        var max=landscape?LandscapeMaxFontSize:MaximumFontSize;
        var font=Math.Clamp((int)Math.Floor(safe/units),min,max);
        // Long names shrink rather than wrapping beyond frame edges.
        var scale=(int)Math.Floor(safe*100.0/(units*font));
        return (font,Math.Clamp(scale,10,100));
    }

    public static string Format(IReadOnlyList<string> rawWords,
        bool landscape=false)
    {
        if(rawWords.Count==0) return "";
        var words=rawWords.Select(EscapeWord).ToList();
        var single=string.Join(" ",words);
        var one=Fit(VisualUnits(single),landscape);
        // Keep a one-line caption when still legible. Otherwise split only
        // at a word boundary and use at most two lines.
        if(words.Count==1 ||
           (one.FontSize>=(landscape?56:62) && one.ScaleX>=84))
            return "{\\q2\\fs"+one.FontSize+"\\fscx"+one.ScaleX+"}"+single;

        var split=1;
        var best=double.MaxValue;
        for(var i=1;i<words.Count;i++)
        {
            var a=VisualUnits(string.Join(" ",words.Take(i)));
            var b=VisualUnits(string.Join(" ",words.Skip(i)));
            var score=Math.Max(a,b)+Math.Abs(a-b)*0.12;
            if(score<best) { best=score; split=i; }
        }
        var left=string.Join(" ",words.Take(split));
        var right=string.Join(" ",words.Skip(split));
        var fit=Fit(Math.Max(VisualUnits(left),VisualUnits(right)),
            landscape);
        return "{\\q2\\fs"+fit.FontSize+"\\fscx"+fit.ScaleX+
            "}"+left+"\\N"+right;
    }

    /// <summary>
    /// Material layout invariant: even after conservative broad-face
    /// glyph estimation, neither subtitle line exceeds its safe width.
    /// </summary>
    public static bool FitsInsideSafeWidth(IReadOnlyList<string> words,
        bool landscape=false)
    {
        var text=Format(words,landscape);
        var prefixEnd=text.IndexOf('}');
        if(prefixEnd<0)return false;
        var prefix=text[..prefixEnd];
        var fontAt=prefix.IndexOf("\\fs",StringComparison.Ordinal);
        var scaleAt=prefix.IndexOf("\\fscx",StringComparison.Ordinal);
        if(fontAt<0||scaleAt<0)return false;
        if(!int.TryParse(prefix[(fontAt+3)..scaleAt],out var font) ||
            !int.TryParse(prefix[(scaleAt+5)..],out var scale))return false;
        var lines=text[(prefixEnd+1)..].Split("\\N",StringSplitOptions.None);
        return lines.Length<=2 && lines.All(l =>
            VisualUnits(l)*font*scale/100.0<=
                (landscape?LandscapeTextSafeWidth:TextSafeWidth)+1.0);
    }
}
