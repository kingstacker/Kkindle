using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Rewrites only the query sent to the retriever. The original question and
/// conversation are still sent to the answer model unchanged.
/// </summary>
public sealed class QueryRewriteService
{
    private readonly AiChatClient _aiChatClient;
    private readonly Action<string>? _log;

    public QueryRewriteService(
        AiChatClient aiChatClient,
        Action<string>? log = null)
    {
        _aiChatClient = aiChatClient ?? throw new ArgumentNullException(nameof(aiChatClient));
        _log = log;
    }

    public async Task<string> RewriteAsync(
        AiConnectionSettings settings,
        string question,
        IReadOnlyList<AiConversationTurn> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0 || !NeedsRewrite(question)) return question.Trim();

        var recentHistory = history.TakeLast(8).ToArray();
        try
        {
            var rewritten = await _aiChatClient.CompleteAsync(
                    settings,
                    "你是书籍检索查询改写器。结合最近对话，将当前问题改写成一个独立、明确、适合在书籍全文中检索的问题。补全人物、对象和指代关系。只输出改写后的单句查询，不要解释，不要回答问题。",
                    question.Trim(),
                    recentHistory,
                    cancellationToken)
                .ConfigureAwait(false);
            rewritten = Normalize(rewritten);
            if (rewritten.Length == 0) return question.Trim();

            _log?.Invoke($"Query rewrite: original={Limit(question, 120)}, rewritten={Limit(rewritten, 160)}");
            return rewritten;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Query rewrite unavailable: {Limit(exception.Message, 180)}");
            return question.Trim();
        }
    }

    private static bool NeedsRewrite(string question)
    {
        if (question.Length <= 18) return true;
        return question.ContainsAny(
            "他", "她", "它", "这", "那", "其", "刚才", "上述", "前面",
            "后面", "这个事情", "这个方法", "该方法", "同一", "后来");
    }

    private static string Normalize(string value)
    {
        var result = value
            .ReplaceLineEndings(" ")
            .Trim()
            .Trim('`', '"', '“', '”', '‘', '’');
        if (result.StartsWith("改写后的查询：", StringComparison.Ordinal))
            result = result[7..].Trim();
        return result.Length > 300 ? result[..300].TrimEnd() : result;
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";
}

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
}
