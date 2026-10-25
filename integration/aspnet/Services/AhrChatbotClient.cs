using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ahr.Chatbot.RazorIntegration.Services;

public sealed partial class AhrChatbotClient(HttpClient client, IOptions<ChatbotProxyOptions> options)
{
    public async Task<string> GetNavigationAsync(string message, string[] permissions, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var operationToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ServiceToken);
        request.Content = JsonContent.Create(new { message, permissions });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "text/plain")
            throw new ChatbotUnavailableException();

        // Never pass arbitrary upstream HTML, errors or a large body to the browser.
        await using var stream = await response.Content.ReadAsStreamAsync(operationToken);
        var bytes = new byte[2049];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), operationToken);
            if (read == 0) break;
            count += read;
        }
        if (count == bytes.Length) throw new ChatbotUnavailableException();
        var navigation = Encoding.UTF8.GetString(bytes, 0, count);
        if (!IsAllowedNavigation(navigation))
            throw new ChatbotUnavailableException();
        return navigation;
    }

    public static bool IsAllowedNavigation(string value) =>
        value.Length is > 0 and <= 200 && SafePath().IsMatch(value) && !value.Contains("//", StringComparison.Ordinal);

    [GeneratedRegex(@"\A/[A-Za-z0-9_/-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafePath();
}

public sealed class ChatbotUnavailableException : Exception
{
    public ChatbotUnavailableException() : base("The navigation service is unavailable or returned an invalid response.") { }
}
