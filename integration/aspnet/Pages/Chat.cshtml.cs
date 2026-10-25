using System.Text.Json.Serialization;
using Ahr.Chatbot.RazorIntegration.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ahr.Chatbot.RazorIntegration.Pages;

[Authorize]
[RequestSizeLimit(8192)]
public sealed class ChatModel(AhrChatbotClient chatbot) : PageModel
{
    public void OnGet() => Response.Headers.CacheControl = "no-store";

    // Razor Pages validates antiforgery for POST handlers by default. Keep it on.
    public async Task<IActionResult> OnPostSendAsync([FromBody] ChatInput? input, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!ModelState.IsValid || input is null || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 1500)
            return BadRequest();

        // These claims must have been issued by AHR+'s existing authentication.
        // Never bind permissions, employee IDs or roles from the posted JSON.
        var permissions = User.FindAll("permission")
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        try
        {
            var url = await chatbot.GetNavigationAsync(input.Message, permissions, cancellationToken);
            return Content(url, "text/plain; charset=utf-8");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception exception) when (exception is HttpRequestException or ChatbotUnavailableException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ChatInput
{
    public string? Message { get; init; }
}
