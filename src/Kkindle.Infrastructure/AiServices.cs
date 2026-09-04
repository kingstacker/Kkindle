using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class AiConnectionSettings
{
    public string Provider { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey);

    public string ProviderDisplayName => Provider.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "custom" => "自定义 API",
        _ => "DeepSeek"
    };

    public AiConnectionSettings Clone() => new()
    {
        Provider = Provider,
        BaseUrl = BaseUrl,
        Model = Model,
        ApiKey = ApiKey
    };

    public static (string BaseUrl, string Model) GetDefaults(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => ("https://api.openai.com/v1", "gpt-5.6-sol"),
        "custom" => ("http://127.0.0.1:11434/v1", string.Empty),
        _ => ("https://api.deepseek.com", "deepseek-v4-flash")
    };

    public static IReadOnlyList<string> GetModelOptions(string provider, string currentModel)
    {
        var defaults = provider.Trim().ToLowerInvariant() switch
        {
            "openai" => new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5-mini", "gpt-4.1" },
            "deepseek" => new[] { "deepseek-v4-flash", "deepseek-v4-pro" },
            _ => []
        };

        return new[] { currentModel.Trim() }
            .Concat(defaults)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeModel(string provider, string model)
    {
        if (!provider.Trim().Equals("deepseek", StringComparison.OrdinalIgnoreCase))
            return model.Trim();

        return model.Trim().ToLowerInvariant() switch
        {
            "deepseek-chat" or "deepseek-reasoner" => "deepseek-v4-flash",
            var normalized => normalized
        };
    }
}

public sealed class AiSettingsStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AiSettingsStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "ai-settings.json");

    public async Task<AiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new AiConnectionSettings();
        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedAiSettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new AiConnectionSettings();
            var provider = persisted.Provider?.Trim().ToLowerInvariant() ?? "deepseek";
            if (provider is not ("deepseek" or "openai" or "custom")) provider = "custom";
            var defaults = AiConnectionSettings.GetDefaults(provider);
            return new AiConnectionSettings
            {
                Provider = provider,
                BaseUrl = string.IsNullOrWhiteSpace(persisted.BaseUrl) ? defaults.BaseUrl : persisted.BaseUrl.Trim(),
                Model = AiConnectionSettings.NormalizeModel(
                    provider,
                    string.IsNullOrWhiteSpace(persisted.Model) ? defaults.Model : persisted.Model),
                ApiKey = string.IsNullOrWhiteSpace(persisted.ProtectedApiKey)
                    ? string.Empty
                    : Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(persisted.ProtectedApiKey)))
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or System.ComponentModel.Win32Exception)
        {
            return new AiConnectionSettings();
        }
    }

    public async Task SaveAsync(AiConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var persisted = new PersistedAiSettings
        {
            Provider = settings.Provider.Trim().ToLowerInvariant(),
            BaseUrl = settings.BaseUrl.Trim(),
            Model = AiConnectionSettings.NormalizeModel(settings.Provider, settings.Model),
            ProtectedApiKey = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? string.Empty
                : Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(settings.ApiKey.Trim())))
        };
        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private sealed class PersistedAiSettings
    {
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
    }
}

public sealed record AiConversationTurn(string Role, string Content);

public sealed record AiStreamChunk(string Text, string Reasoning);

public sealed class AiChatClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public AiChatClient(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        AiConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(settings.BaseUrl, "models");
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("请先配置 AI API Key。");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body);

        using var document = JsonDocument.Parse(body);
        if ((!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            && (!document.RootElement.TryGetProperty("models", out data)
                || data.ValueKind != JsonValueKind.Array))
            throw new InvalidDataException("AI 服务返回的模型列表格式无法识别。");

        return data.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString()
                        : null)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> CompleteAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        CancellationToken cancellationToken = default)
    {
        var answer = new StringBuilder();
        await foreach (var chunk in StreamAsync(
            settings,
            instructions,
            question,
            history,
            reasoningDepth: "auto",
            cancellationToken: cancellationToken))
        {
            answer.Append(chunk.Text);
        }
        return answer.ToString().Trim();
    }

    public async Task<string> TestConnectionAsync(
        AiConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("请先配置 AI 服务、模型和 API Key。");

        var answer = await CompleteAsync(
            settings,
            "你是 Kkindle 的 AI 连通性检测助手。请简短回答，不要补充解释。",
            "请只回复：连接成功",
            Array.Empty<AiConversationTurn>(),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidDataException("AI 服务返回了空答案。");
        return answer;
    }

    public async IAsyncEnumerable<AiStreamChunk> StreamAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        string reasoningDepth = "auto",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!settings.IsConfigured) throw new InvalidOperationException("请先配置 AI 服务、模型和 API Key。");

        if (settings.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var chunk in StreamOpenAiResponsesAsync(
                settings,
                instructions,
                question,
                history,
                reasoningDepth,
                cancellationToken))
            {
                yield return chunk;
            }
            yield break;
        }

        await foreach (var chunk in StreamChatCompletionsAsync(
            settings,
            instructions,
            question,
            history,
            reasoningDepth,
            cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<AiStreamChunk> StreamChatCompletionsAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        string reasoningDepth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = new List<object> { new { role = "system", content = instructions } };
        messages.AddRange(history.TakeLast(8).Select(turn => (object)new
        {
            role = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            content = Limit(turn.Content, 3500)
        }));
        messages.Add(new { role = "user", content = question });

        var request = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["messages"] = messages,
            ["stream"] = true
        };
        AddChatReasoningOption(request, reasoningDepth);

        var payload = JsonSerializer.Serialize(request);
        var endpoint = BuildEndpoint(settings.BaseUrl, "chat/completions");
        using var response = await SendAsync(endpoint, settings.ApiKey, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, errorBody);
        }

        var producedOutput = false;
        await foreach (var eventData in ReadServerSentEventsAsync(response, cancellationToken))
        {
            var chunk = ParseChatCompletionChunk(eventData);
            if (chunk is null) continue;
            producedOutput = true;
            yield return chunk;
        }

        if (!producedOutput)
            throw new InvalidDataException("AI 服务返回了无法识别的流式响应格式。");
    }

    private async IAsyncEnumerable<AiStreamChunk> StreamOpenAiResponsesAsync(
        AiConnectionSettings settings,
        string instructions,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        string reasoningDepth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var conversation = new StringBuilder();
        foreach (var turn in history.TakeLast(8))
        {
            conversation.Append(turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "助手：" : "读者：")
                .AppendLine(Limit(turn.Content, 3500));
        }
        conversation.Append("读者：").Append(question);

        var request = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["instructions"] = instructions,
            ["input"] = conversation.ToString(),
            ["stream"] = true
        };
        var normalizedDepth = NormalizeReasoningDepth(reasoningDepth);
        if (normalizedDepth != "auto")
            request["reasoning"] = new { effort = normalizedDepth, summary = "auto" };

        var payload = JsonSerializer.Serialize(request);
        var endpoint = BuildEndpoint(settings.BaseUrl, "responses");
        using var response = await SendAsync(endpoint, settings.ApiKey, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, errorBody);
        }

        var producedOutput = false;
        await foreach (var eventData in ReadServerSentEventsAsync(response, cancellationToken))
        {
            var chunk = ParseOpenAiResponseChunk(eventData);
            if (chunk is null) continue;
            producedOutput = true;
            yield return chunk;
        }

        if (!producedOutput)
            throw new InvalidDataException("OpenAI 返回了无法识别的 Responses API 流式响应。");
    }

    private static void AddChatReasoningOption(Dictionary<string, object?> request, string reasoningDepth)
    {
        var normalizedDepth = NormalizeReasoningDepth(reasoningDepth);
        if (normalizedDepth == "auto") return;
        request["reasoning_effort"] = normalizedDepth;
        if (request.TryGetValue("model", out var model)
            && model is string modelName
            && modelName.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
            request["thinking"] = new { type = "enabled" };
    }

    private static string NormalizeReasoningDepth(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        "max" => "max",
        _ => "auto"
    };

    private static async IAsyncEnumerable<string> ReadServerSentEventsAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var eventData = new StringBuilder();
        var plainBody = new StringBuilder();
        var sawServerSentEvent = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventData.Length == 0) continue;
                sawServerSentEvent = true;
                var data = eventData.ToString().Trim();
                eventData.Clear();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) yield break;
                if (data.Length > 0) yield return data;
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                sawServerSentEvent = true;
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line[5..].TrimStart());
            }
            else if (!sawServerSentEvent && !line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                plainBody.AppendLine(line);
            }

            if (eventData.ToString().Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) yield break;
        }

        if (eventData.Length > 0)
        {
            var data = eventData.ToString().Trim();
            if (data.Length > 0 && !data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) yield return data;
        }
        else if (!sawServerSentEvent && plainBody.Length > 0)
        {
            yield return plainBody.ToString().Trim();
        }
    }

    private static AiStreamChunk? ParseChatCompletionChunk(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            var source = choice.TryGetProperty("delta", out var delta) ? delta
                : choice.TryGetProperty("message", out var message) ? message
                : default;
            if (source.ValueKind == JsonValueKind.Undefined) return null;

            var text = ReadTextProperty(source, "content");
            var reasoning = ReadTextProperty(source, "reasoning_content", "reasoning", "thinking", "reasoning_summary");
            return text.Length == 0 && reasoning.Length == 0 ? null : new AiStreamChunk(text, reasoning);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AiStreamChunk? ParseOpenAiResponseChunk(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString() ?? string.Empty
                : string.Empty;

            if (type.Equals("response.output_text.delta", StringComparison.OrdinalIgnoreCase))
                return new AiStreamChunk(ReadTextProperty(root, "delta"), string.Empty);

            if (type.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
            {
                var reasoning = ReadTextProperty(root, "delta", "text", "summary", "content");
                if (reasoning.Length == 0 && root.TryGetProperty("item", out var item))
                    reasoning = ReadTextProperty(item, "summary", "content", "text");
                return reasoning.Length == 0 ? null : new AiStreamChunk(string.Empty, reasoning);
            }

            if (root.TryGetProperty("output_text", out _)
                || root.TryGetProperty("output", out _))
                return ParseOpenAiResponseBody(root);

            var fallbackText = ReadTextProperty(root, "delta");
            return fallbackText.Length == 0 ? null : new AiStreamChunk(fallbackText, string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AiStreamChunk? ParseOpenAiResponseBody(JsonElement root)
    {
        var text = ReadTextProperty(root, "output_text");
        var reasoning = string.Empty;
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var itemType = item.TryGetProperty("type", out var itemTypeValue)
                    && itemTypeValue.ValueKind == JsonValueKind.String
                    ? itemTypeValue.GetString() ?? string.Empty
                    : string.Empty;
                var itemText = ReadTextProperty(item, "text", "content", "summary");
                if (itemType.Contains("reasoning", StringComparison.OrdinalIgnoreCase)) reasoning += itemText;
                else if (text.Length == 0) text += itemText;
            }
        }
        return text.Length == 0 && reasoning.Length == 0 ? null : new AiStreamChunk(text, reasoning);
    }

    private static string ReadTextProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            var text = ReadTextValue(value);
            if (text.Length > 0) return text;
        }
        return string.Empty;
    }

    private static string ReadTextValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(ReadTextValue)),
            JsonValueKind.Object => ReadTextProperty(value, "text", "delta", "summary", "content"),
            _ => string.Empty
        };
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        string apiKey,
        string payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.UserAgent.ParseAdd("Kkindle/1.0");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static Uri BuildEndpoint(string baseUrl, string operation)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("API Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith('/' + operation, StringComparison.OrdinalIgnoreCase)) return new Uri(trimmed);
        return new Uri($"{trimmed}/{operation}");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode) return;
        var message = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) message = error.GetString() ?? string.Empty;
                else if (error.TryGetProperty("message", out var detail)) message = detail.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(message)) message = Limit(body, 500);
        throw new HttpRequestException($"AI 请求失败（{(int)response.StatusCode}）：{message}");
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    public void Dispose() => _httpClient.Dispose();
}
