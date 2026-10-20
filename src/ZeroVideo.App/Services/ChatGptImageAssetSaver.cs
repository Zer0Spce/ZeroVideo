using System.Text.Json;
using Microsoft.Playwright;

namespace ZeroVideo.App.Services;

public static class ChatGptImageAssetSaver
{
    public static async Task<bool> TrySaveFullResolutionPngAsync(
        ILocator image,
        string destination,
        string sceneLabel,
        Action<string>? log = null)
    {
        try
        {
            // Do NOT screenshot the DOM element. ChatGPT overlays Edit/download controls and
            // the composer on top of the rendered image, so a locator screenshot can capture
            // browser UI and is limited to the displayed size. Instead, fetch the actual
            // image resource inside the authenticated ChatGPT page, decode it at its native
            // resolution, and export only those pixels to a lossless PNG.
            var raw = await image.EvaluateAsync<string>("""
async el => {
  try {
    const src = el.currentSrc || el.src || '';
    if (!src) return '';
    const response = await fetch(src, { credentials: 'include', cache: 'no-store' });
    if (!response.ok) return '';
    const blob = await response.blob();
    if (!blob || !blob.size || !blob.type.startsWith('image/')) return '';
    const bitmap = await createImageBitmap(blob);
    const width = bitmap.width || el.naturalWidth || 0;
    const height = bitmap.height || el.naturalHeight || 0;
    if (!width || !height) return '';
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext('2d', { alpha: true });
    ctx.drawImage(bitmap, 0, 0, width, height);
    if (bitmap.close) bitmap.close();
    const dataUrl = canvas.toDataURL('image/png');
    return JSON.stringify({ dataUrl, width, height, sourceBytes: blob.size, sourceType: blob.type });
  } catch {
    return '';
  }
}
""");

            if (string.IsNullOrWhiteSpace(raw)) return false;
            var payload = JsonSerializer.Deserialize<ImageAssetPayload>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is null || string.IsNullOrWhiteSpace(payload.DataUrl)) return false;

            // Refuse thumbnail/preview-sized captures. A scene visual should be suitable for
            // 1080p rendering without relying on a tiny browser preview.
            if ((long)payload.Width * payload.Height < 700_000 || Math.Max(payload.Width, payload.Height) < 1000)
            {
                log?.Invoke($"{sceneLabel}: rejected low-resolution ChatGPT preview ({payload.Width}x{payload.Height}); waiting for the full image asset.");
                return false;
            }

            var comma = payload.DataUrl.IndexOf(',');
            if (comma < 0) return false;
            var bytes = Convert.FromBase64String(payload.DataUrl[(comma + 1)..]);
            if (bytes.Length < 50_000) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, destination, true);

            var sizeMb = bytes.Length / 1024d / 1024d;
            log?.Invoke($"{sceneLabel}: saved original ChatGPT image pixels at {payload.Width}x{payload.Height} ({sizeMb:0.0} MB PNG). No webpage screenshot used.");
            return true;
        }
        catch
        {
            try { if (File.Exists(destination + ".tmp")) File.Delete(destination + ".tmp"); } catch { }
            return false;
        }
    }

    private sealed class ImageAssetPayload
    {
        public string DataUrl { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public long SourceBytes { get; set; }
        public string SourceType { get; set; } = "";
    }
}
