namespace ZeroVideo.App.Services;

public sealed class ChatGptImageGenerationException(string message, bool isRateLimited = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool IsRateLimited { get; } = isRateLimited;
}
