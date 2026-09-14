using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kkindle.Core;

namespace Kkindle;

public partial class SendToKindleWindow
{
    private readonly IKindleWebAuthentication? _authentication;
    private KindleWebAuthenticationSnapshot? _authenticationSnapshot;
    private bool _authenticationBusy;
    private bool _signInNavigationRequested;
    private string? _authenticationMessage;
    private DateTimeOffset _resendAvailableAt;

    private void PresentAuthentication(KindleWebPageSnapshot snapshot, bool resumesSend = false)
    {
        var first = !_signInDisplayed;
        var previous = _authenticationSnapshot;
        _authenticationSnapshot = snapshot.Authentication;
        _sessionReady = false;
        _signInDisplayed = true;
        if (first) _manualWeb = false;

        if (previous?.DocumentId != _authenticationSnapshot?.DocumentId
            || previous?.Step != _authenticationSnapshot?.Step)
        {
            ClearNativeAuthenticationSecrets();
            _authenticationMessage = null;
        }

        if (_authentication is null || _authenticationSnapshot is null)
        {
            if (first) ShowBrowser(true);
        }
        else if (_authenticationSnapshot.Step == "web")
        {
            // A CAPTCHA, account recovery, or an unfamiliar form must remain
            // official. A user who went back to the queue is not pulled out again.
            if (first || AuthenticationPanel.IsVisible)
            {
                _manualWeb = true;
                ShowBrowser(true);
            }
            SetStatus("亚马逊需要额外验证，请在官方页面完成，登录后会返回文件列表。");
            return;
        }
        else
        {
            // A page may initially be treated as official fallback while its
            // CVF controls are still being rendered. If the next probe safely
            // recognizes the native step, switch back to the unified panel
            // unless the user explicitly chose to continue on Amazon.
            if (first || (!_manualWeb && !AuthenticationPanel.IsVisible && !QueuePanel.IsVisible)) ShowAuthentication();
            if (_authenticationSnapshot.Step == "start" && !_manualWeb && !_signInNavigationRequested)
                _ = OpenAuthenticationAsync(showPanel: AuthenticationPanel.IsVisible);
        }

        SetStatus(resumesSend
            ? "请完成亚马逊登录，完成后会继续发送本次文件。"
            : "请登录亚马逊账号，登录后会自动返回文件列表。");
        UpdateAuthenticationText();
        if (!_authenticationBusy && (first || previous?.Step != _authenticationSnapshot?.Step))
            FocusAuthenticationInput();
    }

    private void CompleteAuthentication()
    {
        if (_signInDisplayed) _manualWeb = false;
        _signInDisplayed = false;
        _signInNavigationRequested = false;
        _authenticationSnapshot = null;
        _authenticationMessage = null;
        _resendAvailableAt = default;
        ClearNativeAuthenticationSecrets(clearAccount: true);
    }

    private void ShowAuthentication()
    {
        BrowserPanel.IsVisible = false;
        QueuePanel.IsVisible = false;
        AuthenticationPanel.IsVisible = true;
        ReturnToQueueButton.IsVisible = true;
        UpdateAuthenticationText();
        FocusAuthenticationInput();
    }

    private void ClearNativeAuthenticationSecrets(bool clearAccount = false)
    {
        // Undo is disabled on these controls as well. Credentials are never
        // stored in a model, settings, logging, or the library backup.
        AuthenticationPasswordBox.Text = null;
        AuthenticationCodeBox.Text = null;
        if (clearAccount) AuthenticationAccountBox.Text = null;
    }

    private void FocusAuthenticationInput() => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || _authenticationBusy || !AuthenticationPanel.IsVisible) return;
        var input = _authenticationSnapshot?.Step switch
        {
            "account" or "credentials" => AuthenticationAccountBox,
            "password" => AuthenticationPasswordBox,
            "code" => AuthenticationCodeBox,
            _ => null
        };
        input?.Focus();
    });

    private async Task OpenAuthenticationAsync(bool showPanel = true)
    {
        if (_closed || _authenticationBusy || _authentication is null) return;
        _authenticationBusy = true;
        _signInNavigationRequested = true;
        _signInDisplayed = true;
        _authenticationSnapshot = new() { Step = "start" };
        _authenticationMessage = null;
        ClearNativeAuthenticationSecrets();
        if (showPanel) ShowAuthentication();
        try
        {
            EnsureNetworkAllowed();
            await _authentication.OpenSignInAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Browser errors can contain scripts/URLs. Never display or log them.
            if (!_closed)
                _authenticationMessage = "无法打开登录页面，请检查网络后重新加载。";
        }
        finally
        {
            _authenticationBusy = false;
            if (!_closed) UpdateText();
        }
    }

    private async void AuthenticationSubmitButton_Click(object? sender, RoutedEventArgs e) =>
        await ApplyAuthenticationAsync(KindleWebAuthenticationAction.Submit);

    private async void AuthenticationResendButton_Click(object? sender, RoutedEventArgs e) =>
        await ApplyAuthenticationAsync(KindleWebAuthenticationAction.ResendCode);

    private async void AuthenticationChangeAccountButton_Click(object? sender, RoutedEventArgs e) =>
        await ApplyAuthenticationAsync(KindleWebAuthenticationAction.ChangeAccount);

    private async void AuthenticationInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyAuthenticationAsync(KindleWebAuthenticationAction.Submit);
    }

    private async Task ApplyAuthenticationAsync(KindleWebAuthenticationAction action)
    {
        if (_closed || _authenticationBusy || !AuthenticationPanel.IsVisible
            || _authentication is null || _authenticationSnapshot is not { } before) return;
        if (action == KindleWebAuthenticationAction.Submit && !before.CanSubmit
            || action == KindleWebAuthenticationAction.ResendCode && (!before.CanResend || DateTimeOffset.UtcNow < _resendAvailableAt)
            || action == KindleWebAuthenticationAction.ChangeAccount && !before.CanChangeAccount) return;

        var account = action == KindleWebAuthenticationAction.Submit ? AuthenticationAccountBox.Text?.Trim() ?? "" : "";
        var secret = action != KindleWebAuthenticationAction.Submit ? ""
            : before.Step is "password" or "credentials" ? AuthenticationPasswordBox.Text ?? ""
            : before.Step == "code" ? AuthenticationCodeBox.Text?.Trim() ?? "" : "";
        if (action == KindleWebAuthenticationAction.Submit
            && ((before.Step is "account" or "credentials" && string.IsNullOrWhiteSpace(account))
                || (before.Step is "password" or "credentials" or "code" && secret.Length == 0)))
        {
            _authenticationMessage = "请填写当前步骤需要的信息。";
            UpdateAuthenticationText();
            return;
        }

        _authenticationBusy = true;
        _authenticationMessage = null;
        ClearNativeAuthenticationSecrets();
        UpdateText();
        try
        {
            EnsureNetworkAllowed();
            var accepted = await _authentication.ApplyAuthenticationAsync(before, action, account, secret, _lifetime.Token);
            if (action == KindleWebAuthenticationAction.Submit
                && before.Step is "account" or "credentials"
                && !string.IsNullOrWhiteSpace(account))
                _accountDisplay = account;
            secret = "";
            account = "";
            if (!accepted)
            {
                _authenticationMessage = "登录页面已变化，请稍后重试，或在官方页面继续。";
                return;
            }
            if (action == KindleWebAuthenticationAction.ResendCode)
                _resendAvailableAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

            // Keep controls disabled until Amazon reacts. No credential or OTP
            // submission is ever retried automatically after a timeout.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (!_closed && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250, _lifetime.Token);
                var page = await _page.ReadAsync(_lifetime.Token);
                if (_closed) return;
                if (page.Page == "ready")
                {
                    ApplyAccount(page);
                    _sessionReady = true;
                    CompleteAuthentication();
                    ShowBrowser(false);
                    if (!_busy && !_batchFinished) SetStatus("登录状态有效，检查文件列表后点击发送。");
                    return;
                }
                if (page.Page == "signin" && page.Authentication is { } current)
                {
                    PresentAuthentication(page, resumesSend: _busy && !_batchFinished);
                    if (current.DocumentId != before.DocumentId || current.Step != before.Step
                        || current.Revision != before.Revision)
                    {
                        if (action == KindleWebAuthenticationAction.ResendCode && current.Step == "code" && !current.HasError)
                            _authenticationMessage = "已请求重新发送验证码，请查收。";
                        return;
                    }
                }
                else if (page.Page == "unknown")
                {
                    if (AuthenticationPanel.IsVisible)
                    {
                        _manualWeb = true;
                        ShowBrowser(true);
                    }
                    SetStatus("亚马逊需要额外验证，请在官方页面完成，登录后会返回文件列表。");
                    return;
                }
            }
            _authenticationMessage = "尚未收到亚马逊的确认，请检查网络或在官方页面继续。";
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_closed)
                _authenticationMessage = "登录请求未确认，请检查网络或在官方页面继续。";
        }
        finally
        {
            secret = "";
            account = "";
            if (!_closed)
            {
                try { await _authentication.ClearAuthenticationFieldsAsync(_lifetime.Token); }
                catch { /* Do not expose browser errors or credential-bearing scripts. */ }
            }
            _authenticationBusy = false;
            if (!_closed)
            {
                ClearNativeAuthenticationSecrets();
                LoadingProgress.IsVisible = (_useNativeBrowser && _pageLoading) || _busy;
                UpdateText();
                FocusAuthenticationInput();
            }
        }
    }

    private void AuthenticationWebsiteButton_Click(object? sender, RoutedEventArgs e)
    {
        _manualWeb = true;
        ShowBrowser(true);
    }

    private async void AuthenticationRetryButton_Click(object? sender, RoutedEventArgs e)
    {
        _manualWeb = false;
        await OpenAuthenticationAsync();
    }

    private void UpdateAuthenticationText()
    {
        var state = _authenticationSnapshot;
        var step = state?.Step;
        var usable = !_authenticationBusy && state?.CanSubmit == true;
        AuthenticationTitle.Text = UiText.Get(step switch
        {
            "password" => "输入亚马逊密码",
            "code" => "输入验证码",
            "request-code" => "验证亚马逊账号",
            _ => "登录亚马逊账号"
        });
        AuthenticationHint.Text = UiText.Get(step switch
        {
            "account" or "credentials" => "使用与你的 Kindle 关联的亚马逊账号。",
            "password" => "输入该亚马逊账号的密码，验证后继续。",
            "code" when state?.CodeKind == "email" => "请填写亚马逊发送到你邮箱的验证码。",
            "code" when state?.CodeKind == "sms" => "请填写亚马逊发送到你手机的短信验证码。",
            "code" when state?.CodeKind == "authenticator" => "请填写身份验证器中显示的验证码。",
            "code" => "请填写亚马逊要求的验证码，可在官方页面查看接收方式。",
            "request-code" => "亚马逊需要验证你的账号，点击下方按钮获取验证码。",
            "web" => "请在亚马逊官方页面完成当前验证。",
            _ => "正在连接亚马逊，请稍候…"
        });
        AuthenticationAccountPanel.IsVisible = step is "account" or "credentials";
        AuthenticationPasswordPanel.IsVisible = step is "password" or "credentials";
        AuthenticationCodePanel.IsVisible = step == "code";
        AuthenticationAccountBox.IsEnabled = usable;
        AuthenticationPasswordBox.IsEnabled = usable;
        AuthenticationCodeBox.IsEnabled = usable;
        AuthenticationCodeLabel.Text = UiText.Get(state?.CodeKind == "email" ? "邮箱验证码" : "验证码");
        AuthenticationSubmitButton.Content = UiText.Get(_authenticationBusy ? "正在验证…" : step switch
        {
            "password" or "credentials" => "登录",
            "code" => "验证并继续",
            "request-code" => "获取验证码",
            _ => "继续"
        });
        AuthenticationSubmitButton.IsEnabled = usable;
        AuthenticationSubmitButton.IsVisible = step is "account" or "password" or "credentials" or "code" or "request-code";
        AuthenticationChangeAccountButton.IsVisible = state?.CanChangeAccount == true;
        AuthenticationAuxiliaryActions.IsVisible = state?.CanChangeAccount == true || state?.CanResend == true;
        AuthenticationChangeAccountButton.IsEnabled = !_authenticationBusy;
        AuthenticationResendButton.IsVisible = state?.CanResend == true;
        var remaining = Math.Max(0, (int)Math.Ceiling((_resendAvailableAt - DateTimeOffset.UtcNow).TotalSeconds));
        AuthenticationResendButton.IsEnabled = !_authenticationBusy && remaining == 0;
        AuthenticationResendButton.Content = remaining > 0
            ? UiText.Get("{0} 秒后可重新发送", remaining) : UiText.Get("重新发送验证码");
        AuthenticationRetryButton.IsEnabled = !_authenticationBusy;
        AuthenticationWebsiteButton.IsEnabled = !_authenticationBusy;
        var message = _authenticationMessage ?? (state?.HasError == true
            ? "亚马逊未接受本次输入，请检查后重试，或在官方页面查看详情。" : null);
        AuthenticationErrorText.Text = message is null ? "" : UiText.Get(message);
        AuthenticationErrorText.IsVisible = message is not null;
    }
}
