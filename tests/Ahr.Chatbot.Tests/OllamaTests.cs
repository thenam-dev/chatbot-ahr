using System.Net;
using System.Text;
using System.Text.Json;
using Ahr.Chatbot.Core;
using Ahr.Chatbot.Infrastructure;

namespace Ahr.Chatbot.Tests;

public sealed class OllamaTests
{
    private static readonly NavigationRoute Attendance = new(
        "attendance", "Chấm công", "Xem giờ công của bản thân", "/hr/attendance", ["chấm công"], ["attendance:read:self"]);

    [Fact]
    public async Task RequestUsesOllamaStructuredOutputWithoutSendingApplicationPaths()
    {
        using var client = Client(new StubHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("qwen2.5:3b", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("options").GetProperty("temperature").GetInt32());
            Assert.False(json.RootElement.GetProperty("format").GetProperty("additionalProperties").GetBoolean());
            var allowed = json.RootElement.GetProperty("format").GetProperty("properties").GetProperty("route_id").GetProperty("enum");
            Assert.Equal("attendance", allowed[0].GetString());
            Assert.Equal(JsonValueKind.Null, allowed[1].ValueKind);
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.DoesNotContain("/hr/attendance", messages[0].GetProperty("content").GetString());
            Assert.Equal("user", messages[1].GetProperty("role").GetString());
            Assert.Equal("Where are my hours?", messages[1].GetProperty("content").GetString());
            return Reply("{\"route_id\":\"attendance\"}");
        }));
        var classifier = new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1));
        Assert.Equal("attendance", await classifier.ClassifyAsync("Where are my hours?", [Attendance]));
    }

    [Theory]
    [InlineData("{\"route_id\":\"https://evil.example\"}")]
    [InlineData("{\"route_id\":\"payroll\"}")]
    [InlineData("{\"route_id\":null}")]
    [InlineData("{\"route_id\":\"attendance\",\"answer\":\"private data\"}")]
    [InlineData("{\"route_id\":\"attendance\",\"route_id\":\"attendance\"}")]
    [InlineData("{\"route_id\":12}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task ModelHallucinationsAndWrongFormatsAreRejected(string content)
    {
        using var client = Client(new StubHandler((_, _) => Task.FromResult(Reply(content))));
        var classifier = new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1));
        Assert.Null(await classifier.ClassifyAsync("Find a screen", [Attendance]));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"message\":[]}")]
    [InlineData("{\"message\":{\"content\":12}}")]
    [InlineData("{\"message\":null}")]
    [InlineData("{\"message\":{}}")]
    [InlineData("broken json")]
    public async Task InvalidOllamaEnvelopesFailClosed(string body)
    {
        using var client = Client(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        })));
        Assert.Null(await new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1)).ClassifyAsync("Find it", [Attendance]));
    }

    [Fact]
    public async Task ServiceFailureDoesNotBecomeAChatbotAnswer()
    {
        using var client = Client(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        Assert.Null(await new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1)).ClassifyAsync("Find it", [Attendance]));
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var client = Client(new StubHandler((_, _) => Task.FromResult(Reply(new string('a', 65_536)))));
        Assert.Null(await new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1)).ClassifyAsync("Find it", [Attendance]));
    }

    [Fact]
    public async Task TimeoutReturnsNoClassification()
    {
        using var client = Client(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply("{\"route_id\":\"attendance\"}");
        }));
        Assert.Null(await new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromMilliseconds(100)).ClassifyAsync("Find it", [Attendance]));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var client = Client(new StubHandler((_, _) => throw new InvalidOperationException("Must not send an already canceled request.")));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1)).ClassifyAsync("Find it", [Attendance], source.Token));
    }

    [Theory]
    [InlineData("http://ollama.internal:11434")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("https://user:secret@ollama.example")]
    [InlineData("https://ollama.example/subpath")]
    [InlineData("https://ollama.example?token=secret")]
    [InlineData("https://ollama.example#fragment")]
    public void InsecureOrAmbiguousOllamaOriginsAreRejected(string origin)
    {
        using var client = new HttpClient { BaseAddress = new Uri(origin) };
        Assert.Throws<ArgumentException>(() => new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("https://ollama.example")]
    public void ExplicitHttpsAndLoopbackHttpOriginsAreAccepted(string origin)
    {
        using var client = new HttpClient { BaseAddress = new Uri(origin) };
        _ = new OllamaRouteClassifier(client, "qwen2.5:3b", TimeSpan.FromSeconds(1));
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") };

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } }), Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
