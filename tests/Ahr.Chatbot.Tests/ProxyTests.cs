using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ahr.Chatbot.RazorIntegration.Pages;
using Ahr.Chatbot.RazorIntegration.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Ahr.Chatbot.Tests;

public sealed class ProxyTests
{
    // Synthetic fixture, never used outside mocked HttpClient requests.
    private const string TestServiceToken = "unit-test-only-not-a-deployment-key-0123456789";

    [Fact]
    public async Task ClientSendsServerCredentialAndOnlyTrustedMessageAndPermissions()
    {
        var calls = 0;
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:8787/v1/chat", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(TestServiceToken, request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new[] { "message", "permissions" }, body.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("Hôm nay tôi làm mấy giờ?", body.RootElement.GetProperty("message").GetString());
            Assert.Equal(new[] { "attendance:read:self" }, body.RootElement.GetProperty("permissions")
                .EnumerateArray().Select(permission => permission.GetString()));
            Assert.DoesNotContain(TestServiceToken, body.RootElement.GetRawText());
            return Plain("/hr/attendance");
        });
        var client = CreateClient(http);

        var path = await client.GetNavigationAsync("Hôm nay tôi làm mấy giờ?", ["attendance:read:self"], CancellationToken.None);

        Assert.Equal("/hr/attendance", path);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/hr/attendance")]
    [InlineData("/HR/ABC_Results-2026/")]
    public async Task ClientAcceptsOnlySafePlainTextPath(string path)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Plain(path)));
        Assert.Equal(path, await CreateClient(http).GetNavigationAsync("mở màn hình", [], CancellationToken.None));
    }

    [Theory]
    [InlineData("https://evil.example/hr/attendance")]
    [InlineData("//evil.example/hr/attendance")]
    [InlineData("javascript:alert(1)")]
    [InlineData("<a href='/hr/attendance'>Xem</a>")]
    [InlineData("/hr/%2e%2e/payroll")]
    [InlineData("/hr/attendance?employeeId=other-user")]
    [InlineData("/hr/attendance#private")]
    [InlineData("/hr/../payroll")]
    [InlineData("/hr\\payroll")]
    [InlineData("/hr//payroll")]
    [InlineData("/hr/attendance\n")]
    [InlineData(" /hr/attendance")]
    [InlineData("/hr/attendance\t")]
    [InlineData("")]
    public async Task ClientRejectsMalformedUpstreamNavigation(string body)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Plain(body)));
        await Assert.ThrowsAsync<ChatbotUnavailableException>(() =>
            CreateClient(http).GetNavigationAsync("xem chấm công", [], CancellationToken.None));
    }

    [Fact]
    public async Task ClientRejectsLongInvalidPathWithoutBacktracking()
    {
        var body = "/" + new string('a', 2000) + "!";
        using var http = CreateHttp((_, _) => Task.FromResult(Plain(body)));
        await Assert.ThrowsAsync<ChatbotUnavailableException>(() =>
            CreateClient(http).GetNavigationAsync("mở màn hình", [], CancellationToken.None));
    }

    [Fact]
    public async Task ClientBoundsUpstreamBody()
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Plain("/" + new string('a', 10_000))));
        await Assert.ThrowsAsync<ChatbotUnavailableException>(() =>
            CreateClient(http).GetNavigationAsync("mở màn hình", [], CancellationToken.None));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("application/octet-stream")]
    public async Task ClientRejectsWrongResponseContentType(string mediaType)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("/hr/attendance", Encoding.UTF8, mediaType)
        }));
        await Assert.ThrowsAsync<ChatbotUnavailableException>(() =>
            CreateClient(http).GetNavigationAsync("mở màn hình", [], CancellationToken.None));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ClientRejectsNonSuccessStatusWithoutReturningUpstreamBody(int status)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("upstream-private-error-content", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<ChatbotUnavailableException>(() =>
            CreateClient(http).GetNavigationAsync("mở màn hình", [], CancellationToken.None));
        Assert.DoesNotContain("upstream-private-error-content", error.Message);
    }

    [Fact]
    public async Task PageBuildsPermissionsFromAuthenticatedClaimsOnly()
    {
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new[] { "attendance:read:self" }, body.RootElement.GetProperty("permissions")
                .EnumerateArray().Select(permission => permission.GetString()));
            Assert.DoesNotContain("Administrator", body.RootElement.GetRawText());
            Assert.DoesNotContain("employee-fixture-1", body.RootElement.GetRawText());
            return Plain("/hr/attendance");
        });
        var page = CreatePage(CreateClient(http),
            new Claim("permission", "attendance:read:self"),
            new Claim("permission", "attendance:read:self"),
            new Claim(ClaimTypes.Role, "Administrator"),
            new Claim(ClaimTypes.NameIdentifier, "employee-fixture-1"));

        var result = Assert.IsType<ContentResult>(await page.OnPostSendAsync(
            new ChatInput { Message = "Hôm nay tôi làm mấy giờ?" }, CancellationToken.None));

        Assert.Equal("/hr/attendance", result.Content);
        Assert.StartsWith("text/plain", result.ContentType);
        Assert.Equal("no-store", page.Response.Headers.CacheControl);
    }

    [Fact]
    public void BrowserInputCannotDeclarePermissionsOrEmployeeIdentity()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChatInput>(
            """{"message":"xem nhân sự","permissions":["employees:read"]}""", jsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChatInput>(
            """{"message":"xem chấm công","employeeId":"someone-else"}""", jsonOptions));
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("bad-status")]
    [InlineData("bad-path")]
    public async Task PageMapsServiceFailureToGatewayStatusWithoutExposingDetails(string failure)
    {
        using var http = CreateHttp((_, _) => failure switch
        {
            "http-error" => Task.FromException<HttpResponseMessage>(new HttpRequestException("private connection details")),
            "bad-status" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("private upstream details")
            }),
            _ => Task.FromResult(Plain("https://untrusted.example/"))
        });
        var page = CreatePage(CreateClient(http));
        var result = Assert.IsType<StatusCodeResult>(await page.OnPostSendAsync(
            new ChatInput { Message = "xem chấm công" }, CancellationToken.None));
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task PageMapsUpstreamTimeoutToGatewayTimeout()
    {
        using var http = CreateHttp((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout fixture")));
        var page = CreatePage(CreateClient(http));
        var result = Assert.IsType<StatusCodeResult>(await page.OnPostSendAsync(
            new ChatInput { Message = "xem chấm công" }, CancellationToken.None));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, result.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PageRejectsEmptyMessageBeforeCallingService(string message)
    {
        using var http = CreateHttp((_, _) => throw new Xunit.Sdk.XunitException("Service should not be called"));
        var page = CreatePage(CreateClient(http));
        Assert.IsType<BadRequestResult>(await page.OnPostSendAsync(new ChatInput { Message = message }, CancellationToken.None));
    }

    [Fact]
    public async Task PageRejectsOverlongMessageBeforeCallingService()
    {
        using var http = CreateHttp((_, _) => throw new Xunit.Sdk.XunitException("Service should not be called"));
        var page = CreatePage(CreateClient(http));
        Assert.IsType<BadRequestResult>(await page.OnPostSendAsync(
            new ChatInput { Message = new string('a', 1501) }, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8787/", true)]
    [InlineData("http://localhost:8787/", true)]
    [InlineData("https://chatbot.example/", true)]
    [InlineData("http://chatbot.example/", false)]
    [InlineData("https://user:password@chatbot.example/", false)]
    [InlineData("https://chatbot.example/private", false)]
    [InlineData("https://chatbot.example/?key=value", false)]
    [InlineData("https://chatbot.example/#fragment", false)]
    [InlineData("/relative-service", false)]
    public void ProxyOptionsRequireSafeConfiguredServiceOrigin(string origin, bool expected)
    {
        var options = new ChatbotProxyOptions { BaseUrl = origin, ServiceToken = TestServiceToken };
        Assert.Equal(expected, options.IsValid());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("unit-test-token-with-a-forbidden-space ")]
    [InlineData("unit-test-token-with-a-forbidden-newline\n")]
    public void ProxyOptionsRejectInvalidServerCredential(string token)
    {
        Assert.False(new ChatbotProxyOptions { ServiceToken = token }.IsValid());
    }

    private static AhrChatbotClient CreateClient(HttpClient http) => new(http, Options.Create(new ChatbotProxyOptions
    {
        ServiceToken = TestServiceToken
    }));

    private static HttpClient CreateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
        new(new FakeHandler(responder)) { BaseAddress = new Uri("http://127.0.0.1:8787/") };

    private static HttpResponseMessage Plain(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/plain")
    };

    private static ChatModel CreatePage(AhrChatbotClient client, params Claim[] claims) => new(client)
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "UnitTestTrustedPrincipal"))
            }
        }
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
