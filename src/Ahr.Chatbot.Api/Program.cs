using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Ahr.Chatbot.Core;
using Ahr.Chatbot.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

var demo = args.Contains("--demo", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(arg => arg != "--demo").ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});
var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:8787";
if (demo && urls.Split(';').Any(url => !IsLoopbackUrl(url)))
    throw new InvalidOperationException("Demo mode may only listen on loopback. Use authenticated production mode for deployment.");
builder.WebHost.UseUrls(urls);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});

var serviceToken = builder.Configuration["Chatbot:ServiceToken"];
var validServiceToken = serviceToken is { Length: >= 32 and <= 512 }
    && serviceToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '~');
if (!demo && !validServiceToken)
    throw new InvalidOperationException("Set Chatbot__ServiceToken to 32–512 ASCII letters/digits or _-.~ using secure server configuration.");

var catalogPath = builder.Configuration["Chatbot:RouteCatalog"] ?? "config/routes.example.json";
catalogPath = Path.GetFullPath(catalogPath, AppContext.BaseDirectory);
var catalog = await CatalogLoader.LoadAsync(catalogPath);
var resolverMode = builder.Configuration["Chatbot:Resolver"] ?? "rules";
if (resolverMode is not ("rules" or "ollama"))
    throw new InvalidOperationException("Chatbot__Resolver must be rules or ollama.");

IRouteClassifier? classifier = null;
HttpClient? ollamaClient = null;
if (resolverMode == "ollama")
{
    var baseUrl = builder.Configuration["Ollama:BaseUrl"] ?? "http://127.0.0.1:11434/";
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var ollamaUri)
        || ollamaUri.Scheme is not ("http" or "https")
        || !string.IsNullOrEmpty(ollamaUri.UserInfo)
        || !string.IsNullOrEmpty(ollamaUri.Query)
        || !string.IsNullOrEmpty(ollamaUri.Fragment)
        || ollamaUri.AbsolutePath != "/")
        throw new InvalidOperationException("Ollama__BaseUrl must be an HTTP(S) origin without credentials, path, query or fragment.");
    var timeout = ReadBoundedInt(builder.Configuration, "Ollama:TimeoutSeconds", 10, 1, 60);
    ollamaClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = ollamaUri,
        Timeout = TimeSpan.FromSeconds(timeout + 2)
    };
    classifier = new OllamaRouteClassifier(ollamaClient,
        builder.Configuration["Ollama:Model"] ?? "qwen2.5:3b", TimeSpan.FromSeconds(timeout));
}

var resolver = new NavigationResolver(catalog, classifier);
builder.Services.AddSingleton<INavigationResolver>(resolver);
var rateLimit = ReadBoundedInt(builder.Configuration, "Chatbot:RateLimitPerMinute", 60, 1, 10000);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("chat", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.Lifetime.ApplicationStopped.Register(() => ollamaClient?.Dispose());
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Permissions-Policy"] = "microphone=(self)";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    await next(context);
});
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Json(new { status = "ok", resolver = resolverMode, demo }));
app.MapPost("/v1/chat", async (HttpContext context, INavigationResolver navigation, ILogger<Program> logger) =>
    await HandleChat(context, navigation, logger, isDemo: false))
    .AddEndpointFilter(AuthenticateService)
    .RequireRateLimiting("chat");
MapAsset("/widget.js", "widget.js", "text/javascript; charset=utf-8");
MapAsset("/styles.css", "styles.css", "text/css; charset=utf-8");
if (demo)
{
    MapAsset("/", "index.html", "text/html; charset=utf-8");
    MapAsset("/demo.js", "demo.js", "text/javascript; charset=utf-8");
    app.MapPost("/demo/chat", async (HttpContext context, INavigationResolver navigation, ILogger<Program> logger) =>
        await HandleChat(context, navigation, logger, isDemo: true)).RequireRateLimiting("chat");
}

await app.RunAsync();

void MapAsset(string route, string file, string mime) => app.MapGet(route, () =>
    Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", file), mime));

async ValueTask<object?> AuthenticateService(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
{
    if (!validServiceToken)
        return Results.Problem(statusCode: 503, title: "The authenticated API is not configured in demo mode.");
    var provided = invocation.HttpContext.Request.Headers.Authorization.ToString();
    var expected = "Bearer " + serviceToken;
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
    {
        invocation.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: 401, title: "Service authentication is required.");
    }
    return await next(invocation);
}

static bool IsLoopbackUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
    && uri.Scheme is "http" or "https" && uri.IsLoopback;

static int ReadBoundedInt(IConfiguration config, string name, int fallback, int min, int max)
{
    var raw = config[name];
    if (raw is null) return fallback;
    if (int.TryParse(raw, out var result) && result >= min && result <= max) return result;
    throw new InvalidOperationException($"{name} must be an integer between {min} and {max}.");
}

static async Task<IResult> HandleChat(HttpContext context, INavigationResolver navigation, ILogger logger, bool isDemo)
{
    if (!context.Request.HasJsonContentType())
        return Results.Problem(statusCode: 415, title: "Content-Type must be application/json.");
    ChatRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<ChatRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 8
        }, context.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.Problem(statusCode: 400, title: "Invalid request JSON.");
    }
    catch (BadHttpRequestException ex)
    {
        return Results.Problem(statusCode: ex.StatusCode, title: "Invalid or oversized request.");
    }
    if (request?.Message is not { } message || string.IsNullOrWhiteSpace(message) || message.Length > 1500
        || message.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        return Results.Problem(statusCode: 400, title: "message must contain 1–1500 characters.");
    if (request.Permissions is { } supplied && (supplied.Length > 64 || supplied.Any(p =>
        p is null || !Regex.IsMatch(p, "\\A[a-z][a-z0-9:._-]{0,79}\\z", RegexOptions.CultureInvariant))))
        return Results.Problem(statusCode: 400, title: "Invalid permissions.");
    // These sample employee permissions exist only in explicitly enabled loopback demo mode.
    string[] permissions = isDemo
        ? ["profile:read:self", "attendance:read:self", "leave:read:self", "leave:create:self", "payroll:read:self", "evaluation:read:self"]
        : request.Permissions ?? [];
    try
    {
        var path = await navigation.ResolveAsync(message.Trim(), permissions, context.RequestAborted);
        return Results.Text(path, "text/plain; charset=utf-8");
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(499);
    }
    catch (Exception ex)
    {
        // Do not log employee messages, permission claims, model responses or service credentials.
        logger.LogError("Navigation failed with error type {ErrorType}.", ex.GetType().Name);
        return Results.Problem(statusCode: 503, title: "Navigation is temporarily unavailable.");
    }
}

internal sealed record ChatRequest(string? Message, string[]? Permissions);
public partial class Program { }
