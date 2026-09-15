using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Kkindle.Core;
using Xunit;

namespace Kkindle.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public Task KindleWebNativeLoginPreservesQueueAndRequiresOneSendClick(string language) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Kkindle.App)Avalonia.Application.Current!).ApplyLanguage(language);
        var file = Path.Combine(scope.Paths.Data, "native-login.txt");
        await File.WriteAllTextAsync(file, "Synthetic login test document.");
        var page = new AuthenticationPage();
        var window = new SendToKindleWindow(scope.Paths, page: page) { Width = 760, Height = 540 };
        try
        {
            window.AddFiles([file]);
            window.Show();
            await Until(() => window.FindControl<StackPanel>("AuthenticationAccountPanel")!.IsVisible);
            Assert.False(window.FindControl<Grid>("BrowserPanel")!.IsVisible);
            Assert.Equal(1, page.OpenCount);
            var submit = window.FindControl<Button>("AuthenticationSubmitButton")!;
            await Render();
            AssertWithinWindow(window.FindControl<TextBox>("AuthenticationAccountBox")!, window);
            AssertWithinWindow(submit, window);
            await CaptureAuthentication(window, "account", language);

            window.FindControl<TextBox>("AuthenticationAccountBox")!.Text = "synthetic@example.invalid";
            ClickAuthentication(submit);
            ClickAuthentication(submit); // An Enter/double click cannot submit twice.
            await Until(() => window.FindControl<TextBox>("AuthenticationPasswordBox")!.IsEnabled);
            Assert.Equal(1, page.AuthenticationSubmissions);
            Assert.Equal('●', window.FindControl<TextBox>("AuthenticationPasswordBox")!.PasswordChar);
            await CaptureAuthentication(window, "password", language);

            var password = window.FindControl<TextBox>("AuthenticationPasswordBox")!;
            password.Text = "synthetic-password";
            ClickAuthentication(submit);
            Assert.True(string.IsNullOrEmpty(password.Text));
            Assert.False(password.IsUndoEnabled);
            await Until(() => window.FindControl<TextBox>("AuthenticationCodeBox")!.IsEnabled);
            Assert.Equal(language == "zh-CN" ? "邮箱验证码" : "Email verification code",
                window.FindControl<TextBlock>("AuthenticationCodeLabel")!.Text);
            await CaptureAuthentication(window, "code", language);

            var code = window.FindControl<TextBox>("AuthenticationCodeBox")!;
            code.Text = "000000";
            ClickAuthentication(submit);
            await Until(() => submit.IsEnabled && window.FindControl<TextBlock>("AuthenticationErrorText")!.IsVisible);
            Assert.True(string.IsNullOrEmpty(code.Text));
            Assert.False(code.IsUndoEnabled);
            var resend = window.FindControl<Button>("AuthenticationResendButton")!;
            ClickAuthentication(resend);
            await Until(() => page.ResendCount == 1 && submit.IsEnabled);
            ClickAuthentication(resend);
            Assert.Equal(1, page.ResendCount);
            Assert.False(resend.IsEnabled);

            code.Text = "012345";
            ClickAuthentication(submit);
            await Until(() => window.FindControl<Button>("SendFilesButton")!.IsEnabled);
            Assert.False(window.FindControl<Grid>("BrowserPanel")!.IsVisible);
            Assert.False(window.FindControl<Grid>("AuthenticationPanel")!.IsVisible);
            Assert.Equal("synthetic@example.invalid", window.FindControl<Button>("AccountButton")!.Content);
            Assert.True(string.IsNullOrEmpty(code.Text));
            Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("AuthenticationAccountBox")!.Text));
            Assert.Single(window.FindControl<ItemsControl>("FileList")!.Items);
            Assert.Equal(0, page.SendCount);
            Assert.Equal(0, page.StageCount);

            ClickAuthentication(window.FindControl<Button>("SendFilesButton")!);
            await Until(() => window.FindControl<ItemsControl>("FileList")!.Items.Cast<KindleWebQueueItem>().Single().State == "submitted");
            Assert.Equal(1, page.SendCount);
            Assert.Equal(1, page.StageCount);
        }
        finally { window.CloseForShutdown(); }
    });

    [Fact]
    public Task KindleWebNativeLoginAllowsChangingAccountAndManualFallback() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var page = new AuthenticationPage { Step = "password" };
        var window = new SendToKindleWindow(scope.Paths, page: page);
        try
        {
            window.Show();
            await Until(() => window.FindControl<StackPanel>("AuthenticationPasswordPanel")!.IsVisible);
            ClickAuthentication(window.FindControl<Button>("AuthenticationChangeAccountButton")!);
            await Until(() => window.FindControl<TextBox>("AuthenticationAccountBox")!.IsEnabled);
            Assert.Equal(1, page.ChangeCount);
            page.Step = "web";
            await Until(() => window.FindControl<Grid>("BrowserPanel")!.IsVisible);
            ClickAuthentication(window.FindControl<Button>("ReturnToQueueButton")!);
            var observed = page.ReadCount;
            await Until(() => page.ReadCount > observed + 1);
            Assert.True(window.FindControl<Grid>("QueuePanel")!.IsVisible);
            Assert.False(window.FindControl<Grid>("BrowserPanel")!.IsVisible);
            page.Step = "ready";
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text == UiText.Get("登录状态有效，检查文件列表后点击发送。"));
            Assert.Equal(0, page.SendCount);
        }
        finally { window.CloseForShutdown(); }
    });

    [Fact]
    public Task KindleWebNativeLoginClearsSecretsWhenHiddenOrClosed() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var page = new AuthenticationPage { Step = "password" };
        var window = new SendToKindleWindow(scope.Paths, page: page);
        try
        {
            window.Show();
            var password = window.FindControl<TextBox>("AuthenticationPasswordBox")!;
            await Until(() => password.IsEnabled);
            password.Text = "synthetic-secret";
            ClickAuthentication(window.FindControl<Button>("ReturnToQueueButton")!);
            Assert.True(string.IsNullOrEmpty(password.Text));
            var count = page.ReadCount;
            await Until(() => page.ReadCount > count);
            Assert.True(window.FindControl<Grid>("QueuePanel")!.IsVisible);
            ClickAuthentication(window.FindControl<Button>("AccountButton")!);
            Assert.True(window.FindControl<Grid>("AuthenticationPanel")!.IsVisible);
            password.Text = "synthetic-secret";
            window.CloseForShutdown();
            Assert.True(string.IsNullOrEmpty(password.Text));
            Assert.Equal(0, page.AuthenticationSubmissions);
        }
        finally { window.CloseForShutdown(); }
    });

    [Fact]
    public Task KindleWebLoginFailureNeverDisplaysExceptionCredentialsOrAutomaticallyRetries() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var page = new AuthenticationPage { Step = "password", FailRequest = true };
        var window = new SendToKindleWindow(scope.Paths, page: page);
        try
        {
            window.Show();
            var password = window.FindControl<TextBox>("AuthenticationPasswordBox")!;
            var submit = window.FindControl<Button>("AuthenticationSubmitButton")!;
            await Until(() => password.IsEnabled);
            password.Text = "sensitive-synthetic-exception";
            ClickAuthentication(submit);
            await Until(() => submit.IsEnabled && window.FindControl<TextBlock>("AuthenticationErrorText")!.IsVisible);
            Assert.DoesNotContain("sensitive-synthetic-exception", window.FindControl<TextBlock>("AuthenticationErrorText")!.Text);
            Assert.True(string.IsNullOrEmpty(password.Text));
            var reads = page.ReadCount;
            await Until(() => page.ReadCount > reads);
            Assert.Equal(1, page.AuthenticationSubmissions);
            Assert.True(page.ClearCount > 0);
        }
        finally { window.CloseForShutdown(); }
    });

    private static void ClickAuthentication(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task KindleWebExpiredSessionRespectsTheExistingSendAndCancellation(bool cancelSend) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var file = Path.Combine(scope.Paths.Data, "expired-session.txt");
        await File.WriteAllTextAsync(file, "Synthetic expired session test.");
        var page = new AuthenticationPage { Step = "ready", ExpireOnReset = true };
        var window = new SendToKindleWindow(scope.Paths, page: page);
        try
        {
            window.AddFiles([file]);
            window.Show();
            var send = window.FindControl<Button>("SendFilesButton")!;
            await Until(() => send.IsEnabled);
            ClickAuthentication(send);
            var password = window.FindControl<TextBox>("AuthenticationPasswordBox")!;
            await Until(() => password.IsEnabled);
            Assert.Equal(0, page.StageCount);
            Assert.Equal(0, page.SendCount);
            if (cancelSend)
            {
                ClickAuthentication(window.FindControl<Button>("StopWaitingButton")!);
                await Until(() => !window.FindControl<Button>("StopWaitingButton")!.IsVisible);
            }
            password.Text = "synthetic-password";
            var submit = window.FindControl<Button>("AuthenticationSubmitButton")!;
            ClickAuthentication(submit);
            var code = window.FindControl<TextBox>("AuthenticationCodeBox")!;
            await Until(() => code.IsEnabled);
            code.Text = "012345";
            ClickAuthentication(submit);
            if (cancelSend)
            {
                await Until(() => send.IsEnabled);
                Assert.Equal(0, page.StageCount);
                Assert.Equal(0, page.SendCount);
            }
            else
            {
                await Until(() => page.SendCount == 1 && !window.FindControl<Button>("StopWaitingButton")!.IsVisible);
                Assert.Equal(1, page.StageCount);
                Assert.False(send.IsEnabled);
            }
            Assert.True(window.FindControl<Grid>("QueuePanel")!.IsVisible);
        }
        finally { window.CloseForShutdown(); }
    });

    private static async Task CaptureAuthentication(SendToKindleWindow window, string step, string language)
    {
        await Render();
        var directory = Environment.GetEnvironmentVariable("KKINDLE_SETTINGS_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var image = window.CaptureRenderedFrame();
        image!.Save(Path.Combine(directory, $"kindle-web-login-{step}-{language}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private sealed class AuthenticationPage : IKindleWebPage, IKindleWebAuthentication
    {
        private int _revision;
        private bool _hasError;
        private string[] _names = [];
        public string Step { get; set; } = "start";
        public string Account { get; private set; } = "";
        public bool FailRequest { get; init; }
        public bool ExpireOnReset { get; set; }
        public int ReadCount { get; private set; }
        public int OpenCount { get; private set; }
        public int ClearCount { get; private set; }
        public int AuthenticationSubmissions { get; private set; }
        public int ResendCount { get; private set; }
        public int ChangeCount { get; private set; }
        public int StageCount { get; private set; }
        public int SendCount { get; private set; }

        public Task OpenSignInAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            Step = "account";
            return Task.CompletedTask;
        }

        public async Task<bool> ApplyAuthenticationAsync(KindleWebAuthenticationSnapshot snapshot,
            KindleWebAuthenticationAction action, string account, string secret, CancellationToken cancellationToken)
        {
            if (action == KindleWebAuthenticationAction.ResendCode) ResendCount++;
            else if (action == KindleWebAuthenticationAction.ChangeAccount) ChangeCount++;
            else AuthenticationSubmissions++;
            await Task.Delay(40, cancellationToken);
            if (FailRequest) throw new InvalidOperationException(secret);
            _revision++;
            _hasError = false;
            if (action == KindleWebAuthenticationAction.ChangeAccount) Step = "account";
            else if (action != KindleWebAuthenticationAction.ResendCode)
            {
                if (Step == "account")
                {
                    Assert.Equal("synthetic@example.invalid", account);
                    Account = account;
                    Step = "password";
                }
                else if (Step == "password")
                {
                    Assert.Equal("synthetic-password", secret);
                    Step = "code";
                }
                else if (Step == "code")
                {
                    _hasError = secret != "012345";
                    if (!_hasError) Step = "ready";
                }
            }
            return true;
        }

        public Task ClearAuthenticationFieldsAsync(CancellationToken cancellationToken)
        {
            ClearCount++;
            return Task.CompletedTask;
        }

        public Task<KindleWebPageSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(new KindleWebPageSnapshot
            {
                Page = Step == "ready" ? "ready" : "signin", Account = Step == "ready" ? Account : "", ReadyNames = _names,
                Files = SendCount == 0 ? [] : _names.Select(name => new KindleWebPageFile(name, "submitted")).ToArray(),
                Authentication = Step == "ready" ? null : new()
                {
                    Step = Step, DocumentId = "synthetic-document", Ticket = "synthetic-ticket", Revision = _revision,
                    CanSubmit = Step is "account" or "password" or "code", CanChangeAccount = Step == "password",
                    CanResend = Step == "code", CodeKind = "email", HasError = _hasError
                }
            });
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            if (ExpireOnReset)
            {
                ExpireOnReset = false;
                Step = "password";
            }
            return Task.CompletedTask;
        }
        public Task StageAsync(IReadOnlyList<KindleWebFile> files, CancellationToken cancellationToken)
        {
            StageCount++;
            _names = files.Select(file => file.Name).ToArray();
            return Task.CompletedTask;
        }
        public Task<bool> SubmitAsync(CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(true);
        }
    }
}
