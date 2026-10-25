namespace Ahr.Chatbot.Core;

/// <summary>
/// Maps text to fixed navigation paths. It never queries employee data, executes an HR action,
/// or trusts a model-generated URL. Unknown and unauthorized requests share a public fallback.
/// </summary>
public sealed class NavigationResolver : INavigationResolver
{
    private static readonly string[] UnsupportedPhrases =
    [
        "khong muon", "khong can", "khong xem", "khong mo", "khong truy cap", "khong phai",
        "dung mo", "dung xem", "dung truy cap", "chua muon", "chua can", "chua xem", "chua mo",
        "bo qua", "xoa", "sua", "cap nhat", "phe duyet",
        "tu choi", "chuyen khoan", "tra luong", "sa thai", "doanh so", "hoa hong", "kpi", "commission"
    ];

    private readonly NavigationCatalog _catalog;
    private readonly IRouteClassifier? _classifier;
    private readonly IReadOnlyList<Keyword> _keywords;

    public NavigationResolver(NavigationCatalog catalog, IRouteClassifier? classifier = null)
    {
        _catalog = CatalogLoader.Validate(catalog);
        _classifier = classifier;
        _keywords = _catalog.Routes
            .SelectMany(route => route.Keywords.Select(keyword => new Keyword(route.Id, VietnameseText.Fold(keyword))))
            .ToArray();
    }

    public async Task<string> ResolveAsync(string message, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(message) || message.Length > 4000 || permissions is null)
        {
            return _catalog.FallbackPath;
        }

        var folded = VietnameseText.Fold(message);
        if (folded.Length == 0 || UnsupportedPhrases.Any(phrase => ContainsPhrase(folded, phrase)))
        {
            return _catalog.FallbackPath;
        }

        var granted = new HashSet<string>(permissions, StringComparer.Ordinal);
        var authorized = _catalog.Routes.Where(route => route.Permissions.All(granted.Contains)).ToArray();
        var matches = FindMatches(folded);
        // A longer phrase wins over a generic keyword inside that same phrase, e.g.
        // "tạo đơn nghỉ phép" beats "nghỉ phép". Separate intents never silently win.
        var specific = matches.Where(match => !matches.Any(other =>
            other.Start <= match.Start && other.End >= match.End && other.End - other.Start > match.End - match.Start)).ToArray();
        var candidates = specific.Select(match => match.RouteId).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 1)
        {
            return authorized.FirstOrDefault(route => route.Id == candidates[0])?.Path ?? _catalog.FallbackPath;
        }

        if (candidates.Length > 1 && specific.Any(left => specific.Any(right =>
            left.RouteId != right.RouteId && (left.End <= right.Start || right.End <= left.Start))))
        {
            return _catalog.FallbackPath;
        }

        if (_classifier is null)
        {
            return _catalog.FallbackPath;
        }

        try
        {
            var routeId = await _classifier.ClassifyAsync(message, authorized, cancellationToken);
            // A model is only an intent classifier. The path always comes from this catalog.
            return authorized.FirstOrDefault(route => route.Id == routeId)?.Path ?? _catalog.FallbackPath;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return _catalog.FallbackPath;
        }
        catch (HttpRequestException)
        {
            return _catalog.FallbackPath;
        }
        catch (System.Text.Json.JsonException)
        {
            return _catalog.FallbackPath;
        }
        catch (InvalidDataException)
        {
            return _catalog.FallbackPath;
        }
    }

    private List<Match> FindMatches(string text)
    {
        var padded = " " + text + " ";
        var matches = new List<Match>();
        foreach (var keyword in _keywords)
        {
            var search = " " + keyword.Text + " ";
            var offset = 0;
            int index;
            while ((index = padded.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
            {
                matches.Add(new Match(keyword.RouteId, index, index + search.Length - 1));
                offset = index + 1;
            }
        }

        return matches;
    }

    private static bool ContainsPhrase(string text, string phrase) =>
        (" " + text + " ").Contains(" " + phrase + " ", StringComparison.Ordinal);

    private sealed record Keyword(string RouteId, string Text);
    private sealed record Match(string RouteId, int Start, int End);
}
