using System.Text.Json;
using Ahr.Chatbot.Core;

namespace Ahr.Chatbot.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task ExampleCatalogContainsOnlySafeNavigationAndPublicFallback()
    {
        var catalog = await TestCatalog.LoadExampleAsync();
        Assert.Equal(1, catalog.Version);
        Assert.Contains(catalog.Routes, route => route.Path == catalog.FallbackPath && route.Permissions.Count == 0);
        Assert.All(catalog.Routes, route => Assert.True(CatalogLoader.IsSafePath(route.Path)));
        Assert.Contains(catalog.Routes, route => route.Id == "abc-score");
        Assert.Contains(catalog.Routes, route => route.Id == "abc-rules");
        Assert.DoesNotContain(catalog.Routes, route => route.Id.Contains("commission", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://evil.example/hr")]
    [InlineData("//evil.example/hr")]
    [InlineData("/hr//employees")]
    [InlineData("/hr/../employees")]
    [InlineData("/hr/%2femployees")]
    [InlineData("/hr?redirect=https://evil.example")]
    [InlineData("/hr#fragment")]
    [InlineData("/hr\\employees")]
    [InlineData("/hr\n")]
    [InlineData("/hr ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void UnsafePathsAreRejected(string path)
    {
        Assert.False(CatalogLoader.IsSafePath(path));
        var catalog = TestCatalog.Minimal() with { FallbackPath = path };
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/hr")]
    [InlineData("/hr/payroll/payslips")]
    [InlineData("/HR/Attendance_2026")]
    public void StaticLocalPathsAreAccepted(string path) => Assert.True(CatalogLoader.IsSafePath(path));

    [Fact]
    public void LongInvalidPathIsRejectedWithoutAmbiguousRegexBacktracking() =>
        Assert.False(CatalogLoader.IsSafePath("/" + new string('a', 198) + "!"));

    [Fact]
    public void DuplicateIdentifiersAndPathsAreRejected()
    {
        var catalog = TestCatalog.Minimal();
        var route = catalog.Routes[0];
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with
        {
            Routes = [route, route with { Path = "/other" }]
        }));
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with
        {
            Routes = [route, route with { Id = "other" }]
        }));
    }

    [Fact]
    public void FallbackCannotBePrivateOrMissing()
    {
        var catalog = TestCatalog.Minimal();
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with { FallbackPath = "/missing" }));
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with
        {
            Routes = [catalog.Routes[0] with { Permissions = ["admin:read"] }]
        }));
    }

    [Fact]
    public void CatalogCannotSmuggleNewlinesThroughIdentifiersOrPermissionNames()
    {
        var catalog = TestCatalog.Minimal();
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with
        {
            Routes = [catalog.Routes[0] with { Id = "help\n" }]
        }));
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with
        {
            Routes = [catalog.Routes[0] with { Permissions = ["admin:read\n"] }]
        }));
    }

    [Fact]
    public void ValidationDefensivelyCopiesCollections()
    {
        var keywords = new[] { "trợ giúp" };
        var permissions = Array.Empty<string>();
        var routes = new[] { new NavigationRoute("help", "Trợ giúp", "Điều hướng hỗ trợ", "/hr/help", keywords, permissions) };
        var validated = CatalogLoader.Validate(new NavigationCatalog(1, "/hr/help", routes));

        keywords[0] = "evil";
        routes[0] = routes[0] with { Path = "/evil" };
        Assert.Equal("trợ giúp", validated.Routes[0].Keywords[0]);
        Assert.Equal("/hr/help", validated.Routes[0].Path);
        Assert.Throws<NotSupportedException>(() => ((IList<NavigationRoute>)validated.Routes).Add(routes[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)validated.Routes[0].Keywords)[0] = "evil");
    }

    [Fact]
    public async Task UnknownJsonFieldsAreRejectedInsteadOfSilentlyIgnoringTypos()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahr-catalog-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"version\":1,\"fallbackPath\":\"/hr/help\",\"routes\":[],\"database\":\"ignored?\"}");
            await Assert.ThrowsAsync<CatalogValidationException>(() => CatalogLoader.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OversizedCatalogIsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ahr-catalog-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[1_048_577]);
            await Assert.ThrowsAsync<CatalogValidationException>(() => CatalogLoader.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateAccentNormalizedKeywordsAreRejected()
    {
        var catalog = TestCatalog.Minimal();
        Assert.Throws<CatalogValidationException>(() => CatalogLoader.Validate(catalog with
        {
            Routes = [catalog.Routes[0] with { Keywords = ["trợ giúp", "tro giup"] }]
        }));
    }
}

internal static class TestCatalog
{
    public static readonly string[] SelfPermissions =
    [
        "profile:read:self", "attendance:read:self", "leave:read:self", "leave:create:self",
        "payroll:read:self", "evaluation:read:self"
    ];

    public static NavigationCatalog Minimal() => new(1, "/hr/help",
    [new NavigationRoute("help", "Trợ giúp", "Điều hướng hỗ trợ", "/hr/help", ["trợ giúp"], [])]);

    public static Task<NavigationCatalog> LoadExampleAsync()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "config", "routes.example.json");
            if (File.Exists(path))
            {
                return CatalogLoader.LoadAsync(path);
            }
        }

        throw new FileNotFoundException("Could not locate config/routes.example.json from the test output.");
    }
}
