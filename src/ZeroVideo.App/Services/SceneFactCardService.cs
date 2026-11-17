using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZeroVideo.App.Services;

/// <summary>
/// Last-resort, deterministic, truthful narration scene visual. Used only when
/// external stock, licensed Commons and ChatGPT illustration all fail.
/// A documentary title/quote card avoids fabricated news or unrelated footage.
/// </summary>
public static class SceneFactCardService
{
    public static string Create(PixabayFootageScene scene, string folder)
    {
        Directory.CreateDirectory(folder);
        var filename = Path.Combine(folder, $"scene-{scene.SceneNumber:00}-fact-card.png");
        if (File.Exists(filename) && new FileInfo(filename).Length > 2000)
            return filename;
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Draw(scene, filename));
        return Draw(scene, filename);
    }

    private static string Draw(PixabayFootageScene scene, string file)
    {
        const int width = 1920, height = 1080;
        var dark = scene.Mood is "ominous" or "somber" or "tense";
        var top = dark ? Color.FromRgb(12, 18, 31) : Color.FromRgb(23, 41, 61);
        var bottom = dark ? Color.FromRgb(39, 35, 47) : Color.FromRgb(48, 78, 91);
        var accent = dark ? Color.FromRgb(214, 97, 94) : Color.FromRgb(123, 204, 198);
        var white = new SolidColorBrush(Color.FromRgb(247, 242, 234));
        var secondary = new SolidColorBrush(Color.FromRgb(191, 206, 213));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 35),
                null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(accent), null,
                new Rect(145, 152, 12, 650));
            var label = Text($"SCENE {scene.SceneNumber:00} / DOCUMENTARY CONTEXT",
                34, FontWeights.SemiBold, secondary, 1520);
            dc.DrawText(label, new Point(205, 164));

            // Use only phrases from the actual narration: this slate can never
            // pass as fabricated archival video or a synthetic crime photograph.
            var heading = Text(scene.SearchQuery.ToUpperInvariant(), 71,
                FontWeights.Bold, white, 1450);
            heading.MaxTextHeight = 220;
            heading.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(heading, new Point(205, 260));

            var excerpt = scene.NarrationExcerpt.Trim();
            if (excerpt.Length > 280)
            {
                var cut = excerpt.LastIndexOf(' ', 280);
                excerpt = excerpt[..Math.Max(100, cut)] + "…";
            }
            var body = Text(excerpt, 41, FontWeights.Normal, white, 1430);
            body.MaxTextHeight = 405;
            body.Trimming = TextTrimming.WordEllipsis;
            dc.DrawText(body, new Point(205, 510));

            var footer = Text("NARRATION-BASED TEXT CARD  ·  NOT ARCHIVAL FOOTAGE",
                27, FontWeights.Medium, secondary, 1500);
            dc.DrawText(footer, new Point(205, 985));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file);
        encoder.Save(output);
        return file;
    }

    private static FormattedText Text(string contents, double size,
        FontWeight weight, Brush brush, double maxWidth)
    {
        var text = new FormattedText(contents, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight,
                FontStretches.Normal), size, brush, 1.0);
        text.MaxTextWidth = maxWidth;
        return text;
    }
}
