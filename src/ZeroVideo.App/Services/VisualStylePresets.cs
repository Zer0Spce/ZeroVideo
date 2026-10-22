namespace ZeroVideo.App.Services;

public static class VisualStylePresets
{
    // Comic Book Explainer intentionally uses page/panel composition, unlike the single-frame cinematic comic preset.
    public static IReadOnlyList<string> Names { get; } =
    [
        "Documentary explainer with fast visual variety",
        "VOX-inspired paper/editorial explainer",
        "History documentary",
        "Mystery / investigation",
        "Science explainer",
        "Stickman explainer",
        "Cartoon explainer",
        "Editorial Cartoon Explainer",
        "Cinematic Comic Explainer",
        "Comic Book Explainer",
        "Hand-drawn explainer",
        "Whiteboard explainer",
        "Custom"
    ];

    public static string GetPrompt(string? style)
    {
        return style switch
        {
            "VOX-inspired paper/editorial explainer" =>
                "VOX-inspired editorial paper-cutout look: tactile newspaper and magazine fragments, torn paper edges, monochrome documentary photography, photocopy grain, halftone texture, layered paper depth, restrained marker/print accents, clear editorial composition. Do not make a generic clean infographic.",

            "History documentary" =>
                "Cinematic historical-documentary visual language. Keep clothing, architecture, technology, objects, geography, and era details historically plausible. Prefer archival-photo feeling, period documents, maps, restrained reconstruction imagery, and realistic documentary lighting.",

            "Mystery / investigation" =>
                "Investigative documentary visual language: evidence, locations, maps, documents, archival imagery, subtle suspense, controlled dramatic lighting, clear visual clues, and grounded realism. Avoid supernatural additions unless the narration explicitly requires them.",

            "Science explainer" =>
                "Clear science-explainer visual language: accurate physical relationships, clean diagrams or generated explanatory scenes where useful, readable spatial hierarchy, realistic scale, and visually simple cause-and-effect storytelling. No labels or readable text inside generated images.",

            "Stickman explainer" =>
                "STRICT STICKMAN-ONLY VISUAL STYLE. Every human or character must be a clean 2D stickman, never a realistic person. Use round white heads, simple black oval eyes, expressive eyebrows and mouths, thick clean black outlines, simple stick limbs, flat 2D shapes, minimal props and backgrounds, clear educational composition, and consistent character proportions. Do not generate photorealistic humans, realistic faces, 3D people, or detailed human anatomy.",

            "Cartoon explainer" =>
                "Clean 2D cartoon explainer style with simplified shapes, expressive characters, strong silhouettes, controlled flat color areas, light stylized shading, clear visual storytelling, consistent character design, and uncluttered backgrounds. Avoid photorealistic people and photographic rendering.",

            "Editorial Cartoon Explainer" =>
                "Polished editorial cartoon documentary style for long-form YouTube storytelling: bold clean black outlines, expressive slightly exaggerated human faces and poses, colorful hand-illustrated environments, simplified but recognizable architecture, technology and props, dynamic perspective, strong foreground/midground/background separation, cinematic composition, warm cel-shaded lighting, and clear story-driven scenes. Keep characters and environments fully illustrated rather than photorealistic or 3D. Do not add thumbnail headlines, captions, logos, UI, watermarks, or readable text unless the scene explicitly requires a historically necessary sign or document.",

            "Cinematic Comic Explainer" =>
                "CINEMATIC COMIC EXPLAINER STYLE. Create a polished high-detail 2D digital comic illustration for long-form YouTube storytelling. Use bold clean black ink outlines around characters and important objects, expressive slightly exaggerated caricature faces, dynamic believable poses, detailed cel shading blended with smooth painted gradient lighting, cinematic rim light, warm highlights, deep colorful shadows, and vivid controlled color grading. Build strong foreground, midground and background depth with dynamic wide-angle compositions similar to premium illustrated documentary artwork or a modern comic-book movie poster. Keep vehicles, architecture, technology, clothing and environments recognizable and detailed. Fully illustrated, never photorealistic, never 3D, never anime, never watercolor. Do not add thumbnail headlines, captions, logos, UI, watermarks or readable text unless historically essential to the scene.",

            "Comic Book Explainer" =>
                "TRUE CLASSIC PRINTED COMIC-BOOK PAGE STYLE — NOT polished digital illustration. Make the finished 16:9 scene visibly resemble a physically printed vintage American superhero/pulp comic page from roughly the 1950s-1970s. COMIC PAGE COMPOSITION: strongly favor 2-4 visual panels inside the single finished image whenever the narration contains multiple beats or details; use thick black panel gutters and separator lines, bold rectangular or angled panel borders, dynamic diagonal divisions, inset close-up panels, reaction/detail panels, and one dominant hero panel. The panels must all illustrate moments/details from the SAME current narration scene, never future scenes. Vary panel geometry from image to image so the video does not look like the same grid repeated. Keep panels large and readable at 1080p. REQUIRED PRINT TRAITS: very heavy black ink contours; thick-to-thin brush linework; large solid black shadow shapes; hard graphic cel shading; dense crosshatching and feathered ink strokes in faces, clothing, smoke, metal and architecture; clearly visible Ben-Day dots / halftone screens in skin, skies, shadows and color fills; coarse four-color CMYK printing texture; slight off-register print imperfections; aged off-white pulp-paper grain; limited vintage comic palette dominated by strong reds, yellows, blues, cyan and warm cream; flat color separations rather than smooth gradients; dramatic foreshortening; extreme perspective; energetic action poses; speed lines, impact rays, smoke and graphic motion marks when appropriate. Faces must read as hand-inked classic comic art with strong brows, noses, cheek shadows and expressive mouths. Backgrounds should use simplified inked buildings, vehicles and environments with bold perspective, not photorealistic detail. The result must read immediately as an actual printed comic PAGE, including visible panel separation/gutters when appropriate, not merely a modern digital illustration with outlines. STRICTLY AVOID: glossy digital painting, soft airbrush gradients, painterly concept art, clean modern vector illustration, photorealism, 3D rendering, anime/manga styling, children's-cartoon softness, cinematic photo-real lighting, or smooth realistic skin. No speech balloons, readable captions, headline text, logos, UI or watermarks unless the narration explicitly requires a historically necessary sign or document.",

            "Hand-drawn explainer" =>
                "Hand-drawn editorial explainer style: organic ink or pencil lines, intentionally imperfect strokes, sketchbook texture, lightly textured paper, simple hand-rendered shading, clear visual hierarchy, and coherent illustration throughout. Avoid glossy 3D rendering and photorealism.",

            "Whiteboard explainer" =>
                "Whiteboard explainer style: clean white background, hand-drawn marker line art, simple black outlines with only a few restrained accent colors, diagram-like visual storytelling, simple characters and objects, lots of negative space, and consistent marker strokes. No photorealistic people, photographic backgrounds, captions, or readable labels.",

            "Documentary explainer with fast visual variety" =>
                "Modern documentary-explainer visual language with strong visual variety: cinematic stills, archival-feeling imagery, maps, documents, diagrams, environmental details, and editorial compositions chosen to match each scene. Keep visuals grounded and clear at YouTube resolution.",

            _ =>
                "Follow the selected project style and scene visual direction consistently. Keep each image visually clear, coherent with the rest of the video, and suitable for long-form YouTube editing."
        };
    }
}
