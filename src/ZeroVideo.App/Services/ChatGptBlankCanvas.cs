using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZeroVideo.App.Services;

/// <summary>
/// Creates a deliberately small empty PNG canvas that can be attached to ChatGPT when
/// its image router mistakenly treats a fresh text-to-image request as edit-only.
/// The canvas contains no reference content; it exists only to give that route a valid source.
/// It stays below ZeroVideo's generated-image candidate thresholds so it can never be
/// mistaken for the finished scene artwork.
/// </summary>
public static class ChatGptBlankCanvas
{
    public static string Ensure(string aspectRatio)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo",
            "browser-assets");
        Directory.CreateDirectory(root);

        var vertical = string.Equals(aspectRatio, "9:16", StringComparison.OrdinalIgnoreCase);
        var width = vertical ? 360 : 640;
        var height = vertical ? 640 : 360;
        var path = Path.Combine(root, vertical ? "blank-router-v2-9x16.png" : "blank-router-v2-16x9.png");
        if (File.Exists(path) && new FileInfo(path).Length > 1_000) return path;

        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)255);

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
