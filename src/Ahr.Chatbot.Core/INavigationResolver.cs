namespace Ahr.Chatbot.Core;

public interface INavigationResolver
{
    Task<string> ResolveAsync(string message, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken = default);
}

public interface IRouteClassifier
{
    Task<string?> ClassifyAsync(string message, IReadOnlyList<NavigationRoute> authorizedRoutes, CancellationToken cancellationToken = default);
}
