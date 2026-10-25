namespace Ahr.Chatbot.RazorIntegration.Services;

public sealed class ChatbotProxyOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8787/";
    public string ServiceToken { get; set; } = "";

    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(ServiceToken) && ServiceToken.Length is >= 32 and <= 512 &&
        ServiceToken.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '~') &&
        IsSafeOrigin(BaseUrl);

    internal static bool IsSafeOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/" ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return false;
        return true;
    }
}
