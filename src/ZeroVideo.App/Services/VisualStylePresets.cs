namespace ZeroVideo.App.Services;

public static class VisualStylePresets
{
    public static IReadOnlyList<string> Names { get; } =
    [
        "Documentary explainer with fast visual variety",
        "VOX-inspired paper/editorial explainer",
        "History documentary",
        "Mystery / investigation",
        "Science explainer",
        "Stickman explainer",
        "Cartoon explainer",
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
