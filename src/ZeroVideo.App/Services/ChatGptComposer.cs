using Microsoft.Playwright;

namespace ZeroVideo.App.Services;

/// <summary>
/// Robust helpers for ChatGPT's React-driven composer. The composer can be replaced
/// while the home page initializes, so these helpers never keep a stale locator
/// across focus/fill/send operations.
/// </summary>
public static class ChatGptComposer
{
    public static async Task EnterPromptAsync(
        IPage page,
        IEnumerable<string> selectors,
        string text,
        CancellationToken ct)
    {
        var selectorList = selectors.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        if (selectorList.Length == 0)
            throw new InvalidOperationException("No ChatGPT prompt selectors are configured.");

        var started = DateTime.UtcNow;
        Exception? lastError = null;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromSeconds(45))
        {
            try
            {
                // Focus the current live composer through the DOM instead of clicking a
                // locator that ChatGPT may replace during hydration/re-rendering.
                var focused = await page.EvaluateAsync<bool>("""
                    (selectors) => {
                      const visible = (el) => {
                        if (!el) return false;
                        const r = el.getBoundingClientRect();
                        const s = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
                      };
                      for (const selector of selectors) {
                        for (const el of document.querySelectorAll(selector)) {
                          if (!visible(el)) continue;
                          el.focus();
                          if (el instanceof HTMLTextAreaElement || el instanceof HTMLInputElement) {
                            const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                            const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                            if (setter) setter.call(el, ''); else el.value = '';
                            el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'deleteContentBackward', data: null }));
                          } else if (el.isContentEditable) {
                            el.textContent = '';
                            el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'deleteContentBackward', data: null }));
                          }
                          return true;
                        }
                      }
                      return false;
                    }
                    """, selectorList);

                if (!focused)
                {
                    await Task.Delay(350, ct);
                    continue;
                }

                // Keyboard.InsertText talks to whichever composer is focused *now*, so
                // it is not tied to an element that may have just been detached.
                await page.Keyboard.InsertTextAsync(text);

                var hasText = await page.EvaluateAsync<bool>("""
                    (selectors) => {
                      const visible = (el) => {
                        if (!el) return false;
                        const r = el.getBoundingClientRect();
                        return r.width > 0 && r.height > 0;
                      };
                      for (const selector of selectors) {
                        for (const el of document.querySelectorAll(selector)) {
                          if (!visible(el)) continue;
                          const value = (el instanceof HTMLTextAreaElement || el instanceof HTMLInputElement)
                            ? el.value
                            : (el.textContent || '');
                          if (value.trim().length > 0) return true;
                        }
                      }
                      return false;
                    }
                    """, selectorList);

                if (hasText) return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(400, ct);
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"ChatGPT prompt box kept changing before ZeroVideo could enter text. " +
            $"Refresh the visible ChatGPT page and try again.{(lastError is null ? "" : " Last error: " + lastError.Message)}");
    }

    public static async Task SendAsync(
        IPage page,
        IEnumerable<string> sendButtonSelectors,
        CancellationToken ct)
    {
        var selectors = sendButtonSelectors.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromSeconds(12))
        {
            try
            {
                var sent = await page.EvaluateAsync<bool>("""
                    (selectors) => {
                      const visible = (el) => {
                        if (!el) return false;
                        const r = el.getBoundingClientRect();
                        const s = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
                      };
                      for (const selector of selectors) {
                        for (const el of document.querySelectorAll(selector)) {
                          if (!visible(el)) continue;
                          if (el.disabled || el.getAttribute('aria-disabled') === 'true') continue;
                          el.click();
                          return true;
                        }
                      }
                      return false;
                    }
                    """, selectors);

                if (sent) return;
            }
            catch
            {
                // ChatGPT may have re-rendered between DOM lookup and click; retry.
            }

            await Task.Delay(250, ct);
        }

        // Last fallback: the prompt composer is normally still focused and Enter sends.
        await page.Keyboard.PressAsync("Enter");
    }
}
