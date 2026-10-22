from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
chat = ROOT / "src/ZeroVideo.App/Services/ChatGptBrowserService.cs"
prompt = ROOT / "src/ZeroVideo.App/Services/PromptBuilder.cs"
styles = ROOT / "src/ZeroVideo.App/Services/VisualStylePresets.cs"

# --- ChatGPT response handling / JSON recovery ---
s = chat.read_text(encoding="utf-8")
old = '''                    var text = (await item.InnerTextAsync()).Trim();
                    var json = TryExtractLastValidJsonObject(text);
                    if (json is not null) return json;
'''
new = '''                    var text = (await item.InnerTextAsync()).Trim();
                    // Preserve the complete assistant response even if an otherwise-correct
                    // chapter contains an accidentally unescaped quote inside narration.
                    // ExtractJson performs a narrow repair pass later. Returning only a nested
                    // valid object here could turn a five-scene chapter into one scene.
                    if (LooksLikeJsonPayload(text)) return text;
'''
if old not in s:
    raise SystemExit("assistant response extraction block not found")
s = s.replace(old, new, 1)

old_quality = '''- One single image only; no multi-panel layout.
- No captions, no readable text, no logos, no watermark, and no UI.
'''
new_quality = '''- Produce ONE finished image file for this scene. If VIDEO STYLE is Comic Book Explainer, that one image SHOULD use a true comic-page composition when it helps the narration: 2-4 illustrated panels inside the same frame, separated by thick black gutters/borders, angled panel dividers, or inset close-up panels. Keep every panel tied only to THIS scene's narration; do not depict future scenes. Vary the panel layout naturally instead of repeating the same grid every time. For all other styles, use one full-frame composition with no multi-panel layout.
- No captions, no readable text, no logos, no watermark, and no UI. Comic Book Explainer may use empty graphic caption-box shapes only if they contain no readable text, but prefer pure visual panels.
'''
if old_quality not in s:
    raise SystemExit("quality requirement block not found")
s = s.replace(old_quality, new_quality, 1)

old_extract = '''    private static string ExtractJson(string raw)
    {
        var json = TryExtractLastValidJsonObject(raw);
        if (json is null) throw new InvalidDataException("ChatGPT did not return a valid JSON object.");
        return json;
    }
'''
new_extract = r'''    private static bool LooksLikeJsonPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        return first >= 0 && last > first;
    }

    private static string? TryExtractAndRepairTopLevelJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Assistant turns are expected to contain one chapter/outline object. Prefer the
        // widest object first so malformed quoting cannot make us accidentally accept a
        // smaller nested scene object.
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first) return null;

        var candidate = text[first..(last + 1)].Trim();
        if (IsValidJsonObject(candidate)) return candidate;

        var repaired = RepairUnescapedJsonStringQuotes(candidate);
        return IsValidJsonObject(repaired) ? repaired : null;
    }

    private static bool IsValidJsonObject(string candidate)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static string RepairUnescapedJsonStringQuotes(string candidate)
    {
        var output = new System.Text.StringBuilder(candidate.Length + 32);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < candidate.Length; i++)
        {
            var ch = candidate[i];

            if (!inString)
            {
                output.Append(ch);
                if (ch == '"') inString = true;
                continue;
            }

            if (escaped)
            {
                output.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                output.Append(ch);
                escaped = true;
                continue;
            }

            if (ch != '"')
            {
                output.Append(ch);
                continue;
            }

            if (IsLikelyJsonStringTerminator(candidate, i))
            {
                output.Append(ch);
                inString = false;
            }
            else
            {
                // ChatGPT occasionally writes quoted titles inside narration without JSON
                // escaping, e.g. titled "Some Document". Preserve the quote as content.
                output.Append("\\\"");
            }
        }

        return output.ToString();
    }

    private static bool IsLikelyJsonStringTerminator(string text, int quoteIndex)
    {
        var i = quoteIndex + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return true;

        var next = text[i];
        if (next is ':' or '}' or ']') return true;

        if (next == ',')
        {
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) return true;
            // Object properties and string arrays normally begin the next value with a quote.
            // A comma followed by ordinary prose is more likely part of an inner quotation.
            return text[i] is '"' or '}' or ']';
        }

        return false;
    }

    private static string ExtractJson(string raw)
    {
        // First try the complete assistant object, including a narrowly-scoped repair for
        // accidental unescaped quotes inside JSON strings. Only then fall back to the older
        // valid-object scanner for responses that contain surrounding conversation text.
        var json = TryExtractAndRepairTopLevelJsonObject(raw) ?? TryExtractLastValidJsonObject(raw);
        if (json is null) throw new InvalidDataException("ChatGPT did not return a valid JSON object.");
        return json;
    }
'''
if old_extract not in s:
    raise SystemExit("ExtractJson block not found")
s = s.replace(old_extract, new_extract, 1)

s = s.replace(
    'Return one complete valid JSON object only, with all scenes included and no markdown fences.',
    'Return one complete valid JSON object only, with all scenes included and no markdown fences. Escape every double quote that appears inside a JSON string value as \\".',
    1,
)
chat.write_text(s, encoding="utf-8")

# --- Initial chapter prompt JSON safety ---
p = prompt.read_text(encoding="utf-8")
needle = '- Do not fabricate specific quotes, documents, statistics, or events.\n\nReturn ONLY valid JSON, no markdown and no commentary.'
replacement = '- Do not fabricate specific quotes, documents, statistics, or events.\n- JSON SAFETY: if narration or any other string contains a quotation mark, document title, nickname, or quoted phrase, escape every inner double quote as \\\\" so the response remains valid JSON. Never place a raw unescaped double quote inside a JSON string value.\n\nReturn ONLY valid JSON, no markdown and no commentary.'
if needle not in p:
    raise SystemExit("PromptBuilder insertion point not found")
p = p.replace(needle, replacement, 1)
prompt.write_text(p, encoding="utf-8")

# --- Literal classic comic-page composition ---
v = styles.read_text(encoding="utf-8")
old_style = '"TRUE CLASSIC PRINTED COMIC-BOOK STYLE — NOT polished digital illustration. Make the scene look like a vintage American superhero/pulp comic page from roughly the 1950s-1970s, but presented as ONE full-frame cinematic panel. REQUIRED VISUAL TRAITS: very heavy black ink contours; thick-to-thin brush linework; large solid black shadow shapes; hard graphic cel shading; dense crosshatching and feathered ink strokes in faces, clothing, smoke, metal and architecture; clearly visible Ben-Day dots / halftone screens in skin, skies, shadows and color fills; coarse four-color CMYK printing texture; slight off-register print imperfections; aged off-white pulp-paper grain; limited vintage comic palette dominated by strong reds, yellows, blues, cyan and warm cream; flat color separations rather than smooth gradients; dramatic foreshortening; extreme perspective; energetic action poses; speed lines, impact rays, smoke and graphic motion marks when appropriate to the narration. Faces must read as hand-inked comic art with strong brows, noses, cheek shadows and expressive mouths. Backgrounds should use simplified inked buildings, vehicles and environments with bold perspective, not photorealistic detail. The final result must visibly resemble a physically printed classic comic panel at first glance. STRICTLY AVOID: glossy digital painting, soft airbrush gradients, painterly concept art, clean modern vector illustration, photorealism, 3D rendering, anime/manga styling, children\'s-cartoon softness, cinematic photo-real lighting, or smooth realistic skin. No speech balloons, captions, headline text, logos, UI or watermarks unless the narration explicitly requires a historically necessary sign or document."'
new_style = '"TRUE CLASSIC PRINTED COMIC-BOOK PAGE STYLE — NOT polished digital illustration. Make the finished 16:9 scene visibly resemble a physically printed vintage American superhero/pulp comic page from roughly the 1950s-1970s. COMIC PAGE COMPOSITION: strongly favor 2-4 visual panels inside the single finished image whenever the narration contains multiple beats or details; use thick black panel gutters and separator lines, bold rectangular or angled panel borders, dynamic diagonal divisions, inset close-up panels, reaction/detail panels, and one dominant hero panel. The panels must all illustrate moments/details from the SAME current narration scene, never future scenes. Vary panel geometry from image to image so the video does not look like the same grid repeated. Keep panels large and readable at 1080p. REQUIRED PRINT TRAITS: very heavy black ink contours; thick-to-thin brush linework; large solid black shadow shapes; hard graphic cel shading; dense crosshatching and feathered ink strokes in faces, clothing, smoke, metal and architecture; clearly visible Ben-Day dots / halftone screens in skin, skies, shadows and color fills; coarse four-color CMYK printing texture; slight off-register print imperfections; aged off-white pulp-paper grain; limited vintage comic palette dominated by strong reds, yellows, blues, cyan and warm cream; flat color separations rather than smooth gradients; dramatic foreshortening; extreme perspective; energetic action poses; speed lines, impact rays, smoke and graphic motion marks when appropriate. Faces must read as hand-inked classic comic art with strong brows, noses, cheek shadows and expressive mouths. Backgrounds should use simplified inked buildings, vehicles and environments with bold perspective, not photorealistic detail. The result must read immediately as an actual printed comic PAGE, including visible panel separation/gutters when appropriate, not merely a modern digital illustration with outlines. STRICTLY AVOID: glossy digital painting, soft airbrush gradients, painterly concept art, clean modern vector illustration, photorealism, 3D rendering, anime/manga styling, children\'s-cartoon softness, cinematic photo-real lighting, or smooth realistic skin. No speech balloons, readable captions, headline text, logos, UI or watermarks unless the narration explicitly requires a historically necessary sign or document."'
if old_style not in v:
    raise SystemExit("Comic Book Explainer preset not found")
v = v.replace(old_style, new_style, 1)
styles.write_text(v, encoding="utf-8")

print("patched ChatGptBrowserService.cs, PromptBuilder.cs and VisualStylePresets.cs")
