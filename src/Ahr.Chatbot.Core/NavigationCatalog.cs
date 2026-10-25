namespace Ahr.Chatbot.Core;

public sealed record NavigationRoute(
    string Id,
    string Label,
    string Description,
    string Path,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Permissions);

public sealed record NavigationCatalog(
    int Version,
    string FallbackPath,
    IReadOnlyList<NavigationRoute> Routes);

public sealed class CatalogValidationException(string message) : Exception(message);
