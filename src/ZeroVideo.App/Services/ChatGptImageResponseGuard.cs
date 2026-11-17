using Microsoft.Playwright;

namespace ZeroVideo.App.Services;

/// <summary>
/// Recognizes explicit image-generation refusals in NEW assistant turns only.
/// Previously refused prompts in the same persistent chat must not block a
/// later unrelated request.
/// </summary>
public static class ChatGptImageResponseGuard
{
    public static bool IsRefusal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.ToLowerInvariant().Replace('’', '\'');
        return (normalized.Contains("guardrails") &&
                (normalized.Contains("similarity") || normalized.Contains("third-party") ||
                 normalized.Contains("image we created"))) ||
               normalized.Contains("similarity to third-party content") ||
               normalized.Contains("cannot generate this image due to") ||
               normalized.Contains("can't generate this image due to") ||
               normalized.Contains("unable to generate this image due to") ||
               normalized.Contains("couldn't generate that image because it violates");
    }

    public static Task<int> AssistantTurnCountAsync(IPage page)
        => page.Locator("[data-message-author-role='assistant']").CountAsync();

    public static async Task<bool> NewTurnContainsRefusalAsync(
        IPage page, int baselineCount)
    {
        if (baselineCount < 0) return false;
        var turns = page.Locator("[data-message-author-role='assistant']");
        var count = await turns.CountAsync();
        if (count <= baselineCount) return false;
        // Evaluate only NEW responses to avoid replaying a refusal shown in
        // an earlier scene in this same long-lived ChatGPT conversation.
        for (var i = baselineCount; i < count; i++)
        {
            var text = await turns.Nth(i).InnerTextAsync();
            if (IsRefusal(text)) return true;
        }
        return false;
    }
}

public sealed class ChatGptImageRefusedException : Exception
{
    public ChatGptImageRefusedException(string message) : base(message) { }
}
