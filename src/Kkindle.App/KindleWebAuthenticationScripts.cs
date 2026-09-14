using System.Text.Json;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Native login controls submit the existing official forms. No private HTTP
/// login endpoints, cookie extraction, or return of credential field values.
/// The claim form (ap_email_login / ax/claim) was checked on 2026-09-14.
/// Unknown forms and verification methods stay in the official browser.
/// </summary>
internal static class KindleWebAuthenticationScripts
{
    private const string Helpers = """
        const authUrl = value => {
            try {
                const url = new URL(value, location.href);
                return url.protocol === 'https:' && url.host === 'www.amazon.com'
                    && !url.username && !url.password
                    && /^\/(?:-\/[a-z]{2}(?:-[a-z]{2})?\/)?(?:ap\/(?:signin|mfa)\/?|ap\/cvf(?:\/.*)?|ax\/claim\/?)$/i.test(url.pathname);
            } catch { return false; }
        };
        const visible = el => !!el && el.isConnected && el.getClientRects().length > 0
            && getComputedStyle(el).visibility !== 'hidden' && !el.closest('[hidden]');
        const one = (selector, root = document) => {
            const found = Array.from(root.querySelectorAll(selector)).filter(visible);
            return found.length === 1 ? found[0] : null;
        };
        const enabled = el => visible(el) && !el.disabled && !el.closest('[disabled]')
            && el.getAttribute('aria-disabled') !== 'true';
        const safeForm = form => form instanceof HTMLFormElement && authUrl(form.action)
            && form.method.toLowerCase() === 'post' && (!form.target || form.target === '_self');
        const safeControl = (control, requireEnabled = true) => {
            if (!(requireEnabled ? enabled(control) : visible(control))) return false;
            if (control instanceof HTMLAnchorElement) {
                const href = control.getAttribute('href') || '';
                // Amazon uses same-document anchors for some CVF actions. They
                // are safe here because the control is selected from the
                // current, already-validated authentication document.
                return (href === '' || href === '#' || authUrl(control.href))
                    && (!control.target || control.target === '_self');
            }
            return safeForm(control.form)
                && (!control.hasAttribute('formaction') || authUrl(control.formAction))
                && (!control.hasAttribute('formmethod') || control.formMethod.toLowerCase() === 'post')
                && (!control.formTarget || control.formTarget === '_self');
        };
        const controlText = control => [control?.value, control?.textContent,
            control?.getAttribute('aria-label'), control?.getAttribute('title')]
            .filter(value => typeof value === 'string' && value.trim())
            .join(' ').replace(/\s+/g, ' ').trim();
        const isResendText = value => /resend|send again|another code|重新发送|再次发送|重新获取|再发送|重发验证码/i.test(value || '');
        const isSubmitText = value => /(submit|verify|continue|confirm|提交|验证|继续|确认)/i.test(value || '')
            && !isResendText(value)
            && !/cancel|register|send code|获取验证码|请求验证码/i.test(value || '');
        const submitFor = (form, selector) => {
            if (!safeForm(form)) return null;
            const known = one(selector, form);
            if (known) return known;
            const buttons = Array.from(form.querySelectorAll('input[type="submit"], button[type="submit"], '
                + 'button:not([type]), input[type="button"], button[type="button"]')).filter(enabled);
            const preferred = buttons.filter(button => isSubmitText(controlText(button)));
            return preferred.length === 1 ? preferred[0] : buttons.length === 1 ? buttons[0] : null;
        };
        const formOf = field => field?.form || field?.closest?.('form') || null;
        const inspect = () => {
            if (!authUrl(location.href)) return { step: 'web' };
            if (Array.from(document.querySelectorAll('#auth-captcha-guess, #auth-captcha-image, #captchacharacters, '
                + '#cvf-captcha-input, #cvf-captcha-img, iframe[src*="captcha"], input[name="passwordCheck"], input[name="customerName"]')).some(visible))
                return { step: 'web' };
            const account = one('#ap_email_login, #ap_email, input[name="email"]:not([type="hidden"])');
            const password = one('#ap_password, input[name="password"]:not(#auth-credential-autofill-hint):not([type="hidden"])');
            // Amazon has used several CVF/MFA field names over time. Keep the
            // broader matches limited to visible, non-submit controls so an
            // action button can never be mistaken for the OTP field.
            const code = one('#auth-mfa-otpcode, #cvf-input-code, #input-box-otp, '
                + 'input[autocomplete="one-time-code"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[inputmode="numeric"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[name="code"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[name="otpCode"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[name*="otp"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[id*="otp"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[id*="cvf"][id*="code"]:not([type="hidden"]):not([type="submit"]):not([type="button"]), '
                + 'input[name*="code"]:not([type="hidden"]):not([type="submit"]):not([type="button"])');
            let step = 'web', form = null, submit = null;
            if (code && !account && !password) {
                step = 'code'; form = formOf(code);
                submit = submitFor(form, '#auth-signin-button, #cvf-submit-otp-button input[type="submit"], '
                    + 'input#cvf-submit-otp-button, button#cvf-submit-otp-button, '
                    + '[name="cvf_action"][value="verify"], [name="cvf_action"][value="submit"]');
            } else if (password && (!account || account.form === password.form)) {
                step = account ? 'credentials' : 'password'; form = formOf(password);
                submit = submitFor(form, '#signInSubmit');
            } else if (account && !password) {
                step = 'account'; form = formOf(account);
                submit = submitFor(form, '#continue input[type="submit"], input#continue, button#continue');
            } else {
                submit = one('[name="cvf_action"][value="send"], #auth-send-code');
                form = submit?.form;
                if (submit && !Array.from(form?.querySelectorAll('input[type="radio"], select') || []).some(visible))
                    step = 'request-code';
            }
            if (!safeForm(form) || !submit || step === 'web') return { step: 'web' };
            if (['account', 'password', 'credentials'].includes(step)
                && form.name !== 'signIn' && form.id !== 'ap_login_form') return { step: 'web' };
            // Reject unfamiliar required fields instead of submitting a partial challenge.
            const expected = [account, password, code];
            if (Array.from(form.querySelectorAll('input, select, textarea')).some(el => visible(el)
                && !expected.includes(el) && !['hidden', 'submit', 'button', 'checkbox'].includes(el.type)))
                return { step: 'web' };
            const knownResend = one('#auth-resend-code-link, #cvf-resend-link, [name="cvf_action"][value="resend"]');
            const resendCandidates = Array.from(form.querySelectorAll('a, button, input[type="submit"], input[type="button"]'))
                .filter(enabled).filter(control => isResendText(controlText(control)));
            const resend = knownResend || (resendCandidates.length === 1 ? resendCandidates[0] : null);
            const change = one('#ap_change_login_claim, #ap_switch_account_link');
            const errors = Array.from(document.querySelectorAll('#auth-error-message-box, .a-alert-error, '
                + '#auth-email-invalid-claim-alert, #auth-password-missing-alert, #auth-mfa-otpcode-missing-alert'))
                .filter(el => visible(el) && el.textContent.trim());
            const notices = Array.from(document.querySelectorAll('.a-alert-success, #auth-success-message-box, '
                + '#cvf-success-message, #auth-mfa-resend-success-alert')).filter(visible);
            const errorText = errors.concat(notices).map(el => el.textContent).join('|');
            let errorKey = 0;
            for (let i = 0; i < errorText.length; i++) errorKey = (Math.imul(errorKey, 31) + errorText.charCodeAt(i)) | 0;
            const wording = form.textContent || '';
            const codeKind = /e-?mail|邮箱|電子郵件|电子邮件/i.test(wording) ? 'email'
                : /text message|SMS|短信/i.test(wording) ? 'sms'
                : /authenticator|身份验证器|身份驗證器/i.test(wording) ? 'authenticator' : 'unknown';
            return { step, form, account, password, code, submit, resend, change, errorKey,
                // Some forms enable their button only after an input event.
                // Native entry must stay editable so Apply can fill it first.
                canSubmit: safeControl(submit, step === 'request-code')
                    && [account, password, code].filter(Boolean).every(enabled),
                canResend: step === 'code' && safeControl(resend),
                canChangeAccount: (step === 'password' || step === 'credentials') && safeControl(change),
                hasError: errors.length > 0, codeKind };
        };
        const read = () => {
            const current = inspect();
            let state = window.__kkindleAuthentication;
            if (!state || state.document !== document || state.url !== location.href) {
                state = { id: crypto.randomUUID(), document, url: location.href, revision: 0 };
                window.__kkindleAuthentication = state;
            }
            if (state.form !== current.form || state.step !== current.step || state.errorKey !== current.errorKey
                || state.account !== current.account || state.password !== current.password || state.code !== current.code) {
                state.revision++; state.ticket = null;
            }
            Object.assign(state, current);
            state.ticket ||= crypto.randomUUID();
            return { step: current.step, documentId: state.id, ticket: state.ticket, revision: state.revision,
                canSubmit: !!current.canSubmit, canResend: !!current.canResend,
                canChangeAccount: !!current.canChangeAccount, hasError: !!current.hasError,
                codeKind: current.codeKind || 'unknown' };
        };
        const setValue = (field, value) => {
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(field, value);
            field.dispatchEvent(new Event('input', { bubbles: true }));
            field.dispatchEvent(new Event('change', { bubbles: true }));
        };
        """;

    private static string Wrap(string body) => "(() => {\n" + Helpers + "\n" + body + "\n})()";

    public static string Read { get; } = Wrap("try { return read(); } catch { return { step: 'web' }; }");

    public static string Apply(KindleWebAuthenticationSnapshot snapshot, KindleWebAuthenticationAction action,
        string account, string secret) => Wrap($$"""
        try {
            if (!authUrl(location.href)) return false;
            const current = read();
            if (current.documentId !== {{JsonSerializer.Serialize(snapshot.DocumentId)}}
                || current.ticket !== {{JsonSerializer.Serialize(snapshot.Ticket)}}
                || current.step !== {{JsonSerializer.Serialize(snapshot.Step)}}) return false;
            const state = window.__kkindleAuthentication;
            const action = {{JsonSerializer.Serialize(action.ToString())}};
            if (action === 'ResendCode' || action === 'ChangeAccount') {
                const control = action === 'ResendCode' ? state.resend : state.change;
                if (!(action === 'ResendCode' ? current.canResend : current.canChangeAccount) || !safeControl(control)) return false;
                state.ticket = null;
                control.click();
                return true;
            }
            if (action !== 'Submit' || !current.canSubmit) return false;
            const account = {{JsonSerializer.Serialize(account)}};
            const secret = {{JsonSerializer.Serialize(secret)}};
            const fits = (field, value) => typeof value === 'string' && value.length > 0
                && (field.maxLength < 0 || value.length <= field.maxLength);
            if (state.account && !fits(state.account, account)) return false;
            const secretField = state.password || state.code;
            if (secretField && !fits(secretField, secret)) return false;
            state.ticket = null; // Each observed ticket can submit at most once.
            if (state.account) setValue(state.account, account);
            if (secretField) {
                secretField.autocomplete = 'off';
                state.secretField = secretField;
                setValue(secretField, secret);
            }
            const check = inspect();
            if (check.step !== state.step || check.form !== state.form || check.submit !== state.submit
                || !safeControl(state.submit)) {
                if (secretField?.isConnected) setValue(secretField, '');
                return false;
            }
            state.submit.click();
            return true;
        } catch { return false; }
        """);

    public static string ClearSecrets { get; } = Wrap("""
        if (!authUrl(location.href)) return false;
        const state = window.__kkindleAuthentication;
        const field = state?.secretField;
        if (field?.isConnected) setValue(field, '');
        if (state) state.secretField = null;
        return true;
        """);
}
