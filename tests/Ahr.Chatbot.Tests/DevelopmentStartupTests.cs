using System.Net;
using System.Net.Http.Json;

namespace Ahr.Chatbot.Tests;

public sealed class DevelopmentApiFixture : ApiFixture
{
    protected override string EnvironmentName => "Development";
    protected override bool DemoMode => true;
}

// Visual Studio's Development environment loads static-web-asset manifests,
// unlike Production. Exercise actual Kestrel startup and assets in both modes.
public sealed class DevelopmentStartupTests(DevelopmentApiFixture fixture) : IClassFixture<DevelopmentApiFixture>
{
    [Fact]
    public async Task DevelopmentDemoServesPageAndWidgetWithoutProjectWwwroot()
    {
        using var page = await fixture.Anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/demo.js", await page.Content.ReadAsStringAsync());
        using var widget = await fixture.Anonymous.GetAsync("/widget.js");
        Assert.Equal(HttpStatusCode.OK, widget.StatusCode);
        Assert.Contains("mountHRChatbot", await widget.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DevelopmentDemoReturnsNavigationLink()
    {
        using var response = await fixture.Anonymous.PostAsJsonAsync("/demo/chat", new
        {
            message = "Hôm nay tôi đã làm bao nhiêu giờ?"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("/hr/attendance", await response.Content.ReadAsStringAsync());
    }
}
