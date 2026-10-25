using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ahr.Chatbot.Core;

public static partial class CatalogLoader
{
    private const int MaximumFileBytes = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<NavigationCatalog> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await file.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (data.Length + count > MaximumFileBytes)
            {
                throw new CatalogValidationException("The route catalog exceeds 1 MiB.");
            }

            await data.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<NavigationCatalog>(data.ToArray(), JsonOptions);
            return Validate(catalog ?? throw new CatalogValidationException("The route catalog must be a JSON object."));
        }
        catch (JsonException)
        {
            // Avoid echoing catalog contents or unexpected input into startup logs.
            throw new CatalogValidationException("The route catalog is not valid version-1 catalog JSON.");
        }
    }

    public static NavigationCatalog Validate(NavigationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Require(catalog.Version == 1, "The route catalog version must be 1.");
        Require(IsSafePath(catalog.FallbackPath), "fallbackPath must be a safe, static application path.");
        Require(catalog.Routes is { Count: > 0 and <= 64 }, "The route catalog must contain 1 to 64 routes.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var routes = new List<NavigationRoute>();
        foreach (var route in catalog.Routes)
        {
            Require(route is not null, "Each route must be an object.");
            Require(route.Id is not null && IdPattern().IsMatch(route.Id), "Each route id must contain 1 to 64 lowercase letters, digits, or hyphens and start with a letter.");
            Require(ids.Add(route.Id), "Route ids must be unique.");
            Require(IsSafePath(route.Path), "Each route path must be a safe, static application path.");
            Require(paths.Add(route.Path), "Route paths must be unique.");
            Require(IsText(route.Label, 120), "Each route needs a label of 1 to 120 characters.");
            Require(IsText(route.Description, 600), "Each route needs a description of 1 to 600 characters.");
            Require(route.Keywords is { Count: > 0 and <= 32 }, "Each route needs 1 to 32 keyword phrases.");
            Require(route.Permissions is { Count: <= 16 }, "Each route must declare up to 16 required permissions.");

            var keywords = new List<string>();
            var seenKeywords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keyword in route.Keywords)
            {
                Require(IsText(keyword, 120), "Each keyword must contain 1 to 120 printable characters.");
                var folded = VietnameseText.Fold(keyword);
                Require(folded.Length > 0 && seenKeywords.Add(folded), "Keywords within a route must be distinct word phrases after accent normalization.");
                keywords.Add(keyword);
            }

            var permissions = new List<string>();
            var seenPermissions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in route.Permissions)
            {
                Require(permission is not null && PermissionPattern().IsMatch(permission), "Permission names must be lowercase identifiers of at most 80 characters.");
                Require(seenPermissions.Add(permission), "Permissions within a route must be unique.");
                permissions.Add(permission);
            }

            routes.Add(route with
            {
                Keywords = Array.AsReadOnly(keywords.ToArray()),
                Permissions = Array.AsReadOnly(permissions.ToArray())
            });
        }

        Require(routes.Any(route => route.Path == catalog.FallbackPath && route.Permissions.Count == 0),
            "fallbackPath must refer to a catalog route with no required permissions.");

        return catalog with { Routes = Array.AsReadOnly(routes.ToArray()) };
    }

    public static bool IsSafePath(string? path) =>
        path is { Length: > 0 and <= 200 } &&
        !path.Contains("//", StringComparison.Ordinal) &&
        PathPattern().IsMatch(path);

    private static bool IsText(string? value, int maximum) =>
        value is not null && value.Length is > 0 && value.Length <= maximum &&
        value == value.Trim() && !value.Any(char.IsControl);

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string message)
    {
        if (!valid)
        {
            throw new CatalogValidationException(message);
        }
    }

    [GeneratedRegex(@"\A/[A-Za-z0-9_/-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9:._-]{0,79}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionPattern();
}
