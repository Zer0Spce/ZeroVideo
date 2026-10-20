using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZeroVideo.App.Services;

/// <summary>
/// Creates an empty PNG canvas that can be attached to ChatGPT when its image router
/// mistakenly treats a fresh text-to-image request as an edit-only request. The canvas
/// contains no reference content; it exists only to give the edit route a valid source.
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
        var width = vertical ? 720 : 1280;
        var height = vertical ? 1280 : 720;
        var path = Path.Combine(root, vertical ? "blank-9x16.png" : "blank-16x9.png");
        if (File.Exists(path) && new FileInfo(path).Length > 2_000) return path;

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
