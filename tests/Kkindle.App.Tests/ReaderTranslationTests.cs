using System.Net;
using Avalonia.Controls;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Xunit;

namespace Kkindle.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderTranslationTests(SettingsUiSession session)
{
    [Theory]
    [InlineData("empty", "空结果", false)]
    [InlineData("network", "无法连接", false)]
    [InlineData("timeout", "超时", false)]
    [InlineData("legacy", "你好", true)]
    public Task TranslationShowsAReadableResultOrFailure(string response, string expected, bool succeeded) =>
        session.Session.Dispatch<bool>(async () =>
        {
            await using var scope = await ReaderTestWindow.Create();
            using var ai = new AiChatClient();
            using var service = new TranslationService(ai, new Handler(response));
            scope.Field<TranslationService>("_translationService").Dispose();
            scope.Set("_translationService", service);
            scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { NetworkEnabled = true });
            scope.Set("_readerPendingSelection", "Hello");
            scope.Get<Control>("LibraryRoot").IsVisible = false;
            scope.Get<Control>("ReaderRoot").IsVisible = true;
            await ReaderTests.Render();

            await scope.Call<Task>("TranslateReaderSelectionAsync", ReaderTranslationProvider.Google, 0, "");
            await ReaderTests.Render();
            Assert.Contains(expected, scope.Get<TextBlock>("ReaderTranslationResultText").Text);
            Assert.Equal(succeeded, scope.Get<Button>("ReaderTranslationCopyButton").IsEnabled);
            Assert.Equal(succeeded, scope.Get<Button>("ReaderTranslationAnnotateButton").IsEnabled);
            Assert.Contains(succeeded ? "完成" : "重试", scope.Get<TextBlock>("ReaderTranslationStatusText").Text);
            if (!succeeded)
                Assert.Contains("代理设置", scope.Get<TextBlock>("ReaderTranslationResultText").Text);
            scope.Call("HideReaderTranslationPopup", false);
            return true;
        }, CancellationToken.None);

    private sealed class Handler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (response == "network") throw new HttpRequestException("connection failed");
            if (response == "timeout") throw new TaskCanceledException("timeout");
            if (response == "legacy" && request.RequestUri!.Host == "translate.googleapis.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response == "legacy" ? "[[\"你好\",\"en\"]]" : "[[[]]]")
            });
        }
    }
}
