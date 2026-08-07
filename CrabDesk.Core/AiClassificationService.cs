using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CrabDesk.Core;

public sealed record AiClassificationInput(string ItemKey, string DisplayName);

public sealed class AiClassificationService : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public AiClassificationService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        AiClassificationSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, settings, "models");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var models = ReadModels(document.RootElement)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (models.Length == 0)
        {
            throw new InvalidDataException("接口返回成功，但没有识别到模型列表。");
        }
        return models;
    }

    public async Task TestModelConnectivityAsync(
        AiClassificationSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("请先选择或输入模型名称。");
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = settings.Model.Trim(),
            messages = new[]
            {
                new { role = "user", content = "请仅回复 OK。" }
            },
            temperature = 0,
            max_tokens = 1
        });
        using var request = CreateRequest(HttpMethod.Post, settings, "chat/completions");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AiClassificationAssignment>> ClassifyAsync(
        AiClassificationSettings settings,
        IReadOnlyList<AiClassificationInput> items,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("请先选择或输入模型名称。");
        }
        if (items.Count == 0)
        {
            return [];
        }
        var normalizedLabels = labels
            .Select(label => label.Trim())
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedLabels.Length == 0)
        {
            throw new InvalidOperationException("请至少提供一个分类标签。");
        }

        var indexedItems = items.Select((item, index) => new
        {
            id = index.ToString(),
            name = item.DisplayName
        }).ToArray();
        var systemPrompt = string.Join("\n\n",
            string.IsNullOrWhiteSpace(settings.CustomPrompt)
                ? "请仅根据桌面图标名称判断用途。"
                : settings.CustomPrompt.Trim(),
            "只能从用户提供的分类标签中选择，不得创造新标签。" +
            "请只返回 JSON，格式为 {\"items\":[{\"id\":\"0\",\"label\":\"分类标签\"}]}。" +
            "每个 id 最多出现一次；无法判断时选择最接近的标签。不要输出 Markdown。");
        var userContent = JsonSerializer.Serialize(new
        {
            labels = normalizedLabels,
            items = indexedItems
        });
        var payload = JsonSerializer.Serialize(new
        {
            model = settings.Model.Trim(),
            temperature = 0,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        });

        using var request = CreateRequest(HttpMethod.Post, settings, "chat/completions");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var content = ReadMessageContent(document.RootElement);
        using var classification = JsonDocument.Parse(ExtractJsonObject(content));
        var labelLookup = normalizedLabels.ToDictionary(label => label, StringComparer.OrdinalIgnoreCase);
        var assignments = new List<AiClassificationAssignment>();
        var seenIds = new HashSet<int>();
        foreach (var entry in ReadAssignments(classification.RootElement))
        {
            if (!int.TryParse(entry.Id, out var id) || id < 0 || id >= items.Count ||
                !labelLookup.TryGetValue(entry.Label.Trim(), out var normalizedLabel) || !seenIds.Add(id))
            {
                continue;
            }
            assignments.Add(new AiClassificationAssignment(
                items[id].ItemKey,
                items[id].DisplayName,
                normalizedLabel));
        }
        return assignments;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        AiClassificationSettings settings,
        string relativePath)
    {
        if (!Uri.TryCreate(settings.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("请输入有效的 HTTP 或 HTTPS 接口地址。");
        }
        var normalizedBase = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(normalizedBase, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        }
        return request;
    }

    private static IEnumerable<string> ReadModels(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : TryGetArray(root, "data", out var data)
                ? data
                : TryGetArray(root, "models", out var models)
                    ? models
                    : default;
        if (array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                yield return entry.GetString() ?? string.Empty;
                continue;
            }
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (var propertyName in new[] { "id", "name", "model" })
            {
                if (entry.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    yield return value.GetString() ?? string.Empty;
                    break;
                }
            }
        }
    }

    private static string ReadMessageContent(JsonElement root)
    {
        if (!TryGetArray(root, "choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidDataException("模型响应中缺少 choices[0].message.content。");
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray().Select(part =>
                part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : string.Empty));
        }
        throw new InvalidDataException("模型返回的消息内容格式不受支持。");
    }

    private static IEnumerable<(string Id, string Label)> ReadAssignments(JsonElement root)
    {
        JsonElement array = default;
        foreach (var propertyName in new[] { "items", "classifications", "results" })
        {
            if (TryGetArray(root, propertyName, out array))
            {
                break;
            }
        }
        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var id = ReadString(entry, "id") ?? ReadString(entry, "itemId");
                var label = ReadString(entry, "label") ?? ReadString(entry, "category");
                if (id is not null && label is not null)
                {
                    yield return (id, label);
                }
            }
            yield break;
        }
        if (root.TryGetProperty("assignments", out var assignments) &&
            assignments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in assignments.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    yield return (property.Name, property.Value.GetString() ?? string.Empty);
                }
            }
        }
    }

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidDataException("模型没有返回有效的 JSON 分类结果。");
        }
        return content[start..(end + 1)];
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (detail.Length > 800)
        {
            detail = detail[..800] + "…";
        }
        throw new HttpRequestException(
            $"模型接口返回 HTTP {(int)response.StatusCode}" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"：{detail}"));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
