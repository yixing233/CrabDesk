using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CrabDesk.Core;

/// <summary>
/// Fixed-endpoint, opt-in web evidence retrieval for AI classification.
/// Search results are deliberately bounded and remain untrusted input data.
/// </summary>
public sealed class TavilySearchService : IDisposable
{
    public const int MaxItemsPerRun = 8;
    private const int MaxResultsPerItem = 3;
    private const int MaxTitleLength = 180;
    private const int MaxSnippetLength = 360;
    private const int MaxEvidenceLength = 1200;
    private static readonly Uri SearchEndpoint = new("https://api.tavily.com/search");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public TavilySearchService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<IReadOnlyList<AiClassificationInput>> SearchAsync(
        string apiKey,
        IReadOnlyList<AiClassificationInput> items,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AiWebSearchRequestException("Tavily API key is missing.");
        }

        var candidates = items
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayName))
            .GroupBy(item => item.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxItemsPerRun)
            .ToArray();
        var evidenceItems = new List<AiClassificationInput>(candidates.Length);
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = await SearchOneAsync(apiKey, item.DisplayName.Trim(), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(evidence))
            {
                evidenceItems.Add(item with { WebEvidence = evidence });
            }
        }
        return evidenceItems;
    }

    private async Task<string?> SearchOneAsync(string apiKey, string query, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            query,
            search_depth = "basic",
            max_results = MaxResultsPerItem,
            include_answer = false,
            include_raw_content = false,
            include_images = false,
            include_favicon = false,
            auto_parameters = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiWebSearchRequestException($"Tavily endpoint returned HTTP {(int)response.StatusCode}.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ReadEvidence(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiWebSearchRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new AiWebSearchRequestException("Tavily search request failed.", exception);
        }
    }

    private static string? ReadEvidence(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            throw new AiWebSearchRequestException("Tavily response did not contain a results array.");
        }

        var snippets = new List<string>(MaxResultsPerItem);
        foreach (var entry in results.EnumerateArray().Take(MaxResultsPerItem))
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var title = NormalizeText(ReadString(entry, "title"), MaxTitleLength);
            var content = NormalizeText(ReadString(entry, "content"), MaxSnippetLength);
            if (title.Length == 0 && content.Length == 0)
            {
                continue;
            }

            snippets.Add(title.Length == 0
                ? content
                : content.Length == 0
                    ? title
                    : $"{title}: {content}");
        }

        return snippets.Count == 0
            ? null
            : NormalizeText(string.Join("\n", snippets), MaxEvidenceLength);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutControlCharacters = new string(value
            .Where(character => !char.IsControl(character))
            .ToArray());
        var normalized = string.Join(" ", withoutControlCharacters.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
