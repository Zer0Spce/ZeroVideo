using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class AutomationProfileService
{
    public async Task<AutomationProfile> LoadAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Automation profile not found.", path);
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<AutomationProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}
