#if KKINDLE_SWIFT_UI
using System.Text;
using System.Text.Json;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile.Views.Sheets;
using static Kkindle.iOSHead.IOSNativeJson;

namespace Kkindle.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private async Task<object> DispatchReaderAsync(string command, JsonElement args, CancellationToken token)
    {
        // Network work captures the current page, then releases the navigation
        // gate. A slow AI/translation response must not prevent closing a book
        // or changing its typography. Closing cancels these session requests.
        if (command is "reader.translate" or "reader.ai")
        {
            string pageText;
            CancellationTokenSource session;
            await _readerGate.WaitAsync(token);
            try
            {
                var current = _reader ?? throw new InvalidOperationException("请先打开一本书。");
                pageText = current.NativePageText();
                session = CancellationTokenSource.CreateLinkedTokenSource(token,
                    _readerCancellation?.Token ?? CancellationToken.None);
            }
            finally { _readerGate.Release(); }
            using (session)
            {
                if (command == "reader.ai") return await AskAiAsync(args, pageText, session.Token);
                using var operation = BeginOperation();
                var text = Text(args, "text");
                var chinese = text.Count(character => character is >= '一' and <= '鿿');
                var ai = await _shell.Modules.Ai.Settings.LoadAsync(session.Token);
                var translated = await _shell.Modules.Ai.Translation.TranslateAsync(text,
                    ai.IsConfigured ? ReaderTranslationProvider.Ai : ReaderTranslationProvider.Bing,
                    chinese * 3 > text.Length ? "en" : "zh-CN", ai, session.Token);
                return Data(("text", translated));
            }
        }
        await _readerGate.WaitAsync(token);
        try
        {
            var reader = _reader ?? throw new InvalidOperationException("请先打开一本书。");
            using var session = CancellationTokenSource.CreateLinkedTokenSource(token,
                _readerCancellation?.Token ?? CancellationToken.None);
            switch (command)
            {
                case "reader.state": return reader.GetNativeState();
                case "reader.toc": return reader.GetNativeToc();
                case "reader.jump": await reader.NativeJumpToTocAsync(Number(args, "id")); break;
                case "reader.turn": await reader.TurnAsync(Number(args, "direction", 1)); break;
                case "reader.chapter": await reader.NativeMoveChapterAsync(Number(args, "direction", 1)); break;
                case "reader.seek": await reader.NativeSeekAsync(args.GetProperty("progress").GetDouble()); break;
                case "reader.bookmark": await reader.ToggleBookmarkAsync(); break;
                case "reader.search": return await reader.NativeSearchAsync(Text(args, "query"), session.Token);
                case "reader.searchJump": await reader.NativeJumpToSearchAsync(Number(args, "id")); break;
                case "reader.speech": await reader.NativeToggleSpeechAsync(); break;
                case "reader.speechPause": reader.NativePauseSpeech(); break;
                case "reader.speechStop": await reader.NativeStopSpeechAsync(); break;
                case "reader.clearSelection": reader.NativeClearSelection(); break;
                case "reader.annotate": await reader.NativeAnnotateAsync(Text(args, "note")); break;
                case "reader.annotationEdit": await reader.NativeEditAnnotationAsync(Id(args, "id"), Text(args, "note")); break;
                case "reader.annotationDelete": await reader.NativeDeleteAnnotationAsync(Id(args, "id")); break;
                default: throw new InvalidOperationException("未知阅读操作：" + command);
            }
            return Data();
        }
        finally { _readerGate.Release(); }
    }

    private async Task<object> AskAiAsync(JsonElement args, string pageText, CancellationToken token)
    {
        using var operation = BeginOperation();
        var modules = _shell.Modules;
        var settings = await modules.Ai.Settings.LoadAsync(token);
        if (!settings.IsConfigured) throw new InvalidOperationException("请先在“我的”中设置 AI 接口。");
        var book = await modules.Library.Books.GetBookAsync(Id(args, "bookId"), token)
            ?? throw new InvalidOperationException("书籍已不存在。");
        var question = Text(args, "question");
        var sessionId = Text(args, "sessionId");
        var history = args.TryGetProperty("history", out var turns)
            ? turns.Deserialize(IOSNativeJsonContext.Default.ListAiConversationTurn) ?? [] : new List<AiConversationTurn>();
        var bookContext = string.Empty;
        if (!Flag(args, "pageOnly"))
        {
            try
            {
                var query = await modules.Ai.QueryRewrite.RewriteAsync(settings, question, history, token);
                var retrieved = await new MobileAiBookRetrieval(modules, book).RetrieveAsync(query, token);
                bookContext = retrieved.Text;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        var instructions = $"你是 Kkindle 的阅读助手。读者正在读《{book.Title}》。请根据提供的原文，用简洁的中文回答；"
            + "检索只包含部分片段，不能把未命中视为全书没有提及。正文没有涉及时可以用常识补充，但要说明。引用检索片段时注明章节或 PDF 页码。\n\n当前页：\n"
            + pageText + (bookContext.Length == 0 ? "" : "\n\n全书关键词检索片段：\n" + bookContext);
        var answer = new StringBuilder();
        await foreach (var chunk in modules.Ai.ChatClient.StreamAsync(settings, instructions, question,
            history, cancellationToken: token))
        {
            answer.Append(chunk.Text);
            Publish(Data(("type", "aiText"), ("sessionId", sessionId), ("text", answer.ToString())));
        }
        return Data(("text", answer.Length == 0 ? "AI 没有返回内容。" : answer.ToString()));
    }
}
#endif
