using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public sealed class WidgetTranslationService : IDisposable
{
    public const int MaximumInputLength = 12000;
    private const int MaximumConversationTurns = 32;
    private const int MaximumConversationCharacters = 24000;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private readonly object _conversationGate = new();
    private readonly List<ChatMessage> _history = [];
    private ConversationProfile? _profile;
    private int _historyCharacters;
    private long _lastCompletedAt;
    private long _requestVersion;

    private sealed record ChatMessage([property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
    private sealed record ConversationProfile(string Endpoint, string ApiKey, string Model, string Prompt,
        string Language, bool Reuse, int IdleMinutes);

    public WidgetTranslationService(HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> TranslateAsync(WidgetTranslationSettings settings, string text, string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请输入需要翻译的内容。");
        if (text.Length > MaximumInputLength) throw new InvalidOperationException($"每次最多翻译 {MaximumInputLength} 个字符。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("请先在翻译组件设置中配置 AI 接口与模型。");
        if (!Uri.TryCreate(settings.BaseUrl?.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            !string.IsNullOrEmpty(endpoint.UserInfo)) throw new InvalidOperationException("请先配置使用 HTTPS 的 AI 接口地址。");
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new InvalidOperationException("请选择目标语言。");
        var template = string.IsNullOrWhiteSpace(settings.PromptTemplate)
            ? WidgetTranslationSettings.DefaultPromptTemplate : settings.PromptTemplate;
        var prompt = template.Contains("{targetLanguage}", StringComparison.Ordinal)
            ? template.Replace("{targetLanguage}", targetLanguage, StringComparison.Ordinal)
            : template + "\n\n目标语言：" + targetLanguage;
        var profile = new ConversationProfile(endpoint.AbsoluteUri.TrimEnd('/'), settings.ApiKey?.Trim() ?? string.Empty,
            settings.Model.Trim(), prompt, targetLanguage, settings.ReuseConversation, Math.Clamp(settings.ConversationIdleMinutes, 1, 1440));
        List<ChatMessage> messages;
        long version;
        lock (_conversationGate)
        {
            if (_profile != profile || !profile.Reuse ||
                _history.Count > 0 && _clock.GetElapsedTime(_lastCompletedAt) >= TimeSpan.FromMinutes(profile.IdleMinutes) ||
                _history.Count >= MaximumConversationTurns * 2 || _historyCharacters + text.Length > MaximumConversationCharacters)
            {
                ResetConversationLocked();
                _profile = profile;
            }
            version = ++_requestVersion;
            messages = [new("system", profile.Prompt)];
            messages.AddRange(_history);
            messages.Add(new("user", text));
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(profile.Endpoint + "/chat/completions"));
        if (profile.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = profile.Model, stream = false, messages
        }), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"翻译请求失败（HTTP {(int)response.StatusCode}），请检查接口配置。", null, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
            throw new InvalidDataException("翻译接口没有返回译文。");
        var translated = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidDataException("翻译接口返回了空译文。");
        translated = translated.Trim();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_conversationGate)
        {
            if (profile.Reuse && version == _requestVersion && !cancellationToken.IsCancellationRequested)
            {
                if (_historyCharacters + text.Length + translated.Length > MaximumConversationCharacters) ResetConversationLocked();
                else
                {
                    _history.Add(new("user", text)); _history.Add(new("assistant", translated));
                    _historyCharacters += text.Length + translated.Length;
                    _lastCompletedAt = _clock.GetTimestamp();
                }
            }
        }
        return translated;
    }

    public void ResetConversation() { lock (_conversationGate) { ResetConversationLocked(); } }

    private void ResetConversationLocked()
    {
        _requestVersion++;
        _history.Clear(); _historyCharacters = 0; _profile = null;
    }

    public void Dispose() { ResetConversation(); if (_ownsClient) _client.Dispose(); }
}
