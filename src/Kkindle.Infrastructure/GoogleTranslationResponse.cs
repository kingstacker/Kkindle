using System.Text;
using System.Text.Json;

namespace Kkindle.Infrastructure;

internal static class GoogleTranslationResponse
{
    public const string EmptyMessage = "Google 翻译返回了空结果。请检查网络或 Google 代理设置，或切换必应 / AI 后重试。";

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Google 翻译请求超时。请检查网络或 Google 代理设置，或切换必应 / AI 后重试。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException("无法连接 Google 翻译。请检查网络或 Google 代理设置，或切换必应 / AI 后重试。", exception);
        }
    }

    public static string Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException(EmptyMessage);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null
                || (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 0))
                throw new InvalidDataException(EmptyMessage);
            if (root.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Google 翻译返回了无法识别的结果。请检查网络或 Google 代理设置，或切换必应 / AI 后重试。");

            var first = root[0];
            // The legacy endpoint returns [text] for an explicit source language,
            // but [[text, detectedLanguage]] when source detection is enabled.
            if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0
                && first[0].ValueKind == JsonValueKind.String)
                first = first[0];
            if (first.ValueKind == JsonValueKind.String)
                return RequireText(first.GetString());
            if (first.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException(EmptyMessage);
            if (first.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Google 翻译返回了无法识别的结果。");

            var result = new StringBuilder();
            foreach (var segment in first.EnumerateArray())
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0
                    && segment[0].ValueKind == JsonValueKind.String)
                    result.Append(segment[0].GetString());
            return RequireText(result.ToString());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Google 翻译返回了无法识别的结果。请检查网络或 Google 代理设置，或切换必应 / AI 后重试。", exception);
        }
    }

    private static string RequireText(string? text) => !string.IsNullOrWhiteSpace(text)
        ? text : throw new InvalidDataException(EmptyMessage);
}
