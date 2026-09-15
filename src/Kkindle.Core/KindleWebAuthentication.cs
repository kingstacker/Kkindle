namespace Kkindle.Core;

/// <summary>Only form state crosses back from Amazon; never field values or authentication URLs.</summary>
public sealed record KindleWebAuthenticationSnapshot
{
    public string Step { get; init; } = "web";
    public string DocumentId { get; init; } = "";
    public string Ticket { get; init; } = "";
    public int Revision { get; init; }
    public bool CanSubmit { get; init; }
    public bool CanResend { get; init; }
    public bool CanChangeAccount { get; init; }
    public bool HasError { get; init; }
    public string CodeKind { get; init; } = "unknown";
}

public enum KindleWebAuthenticationAction { Submit, ResendCode, ChangeAccount }

public interface IKindleWebAuthentication
{
    Task OpenSignInAsync(CancellationToken cancellationToken);
    Task<bool> ApplyAuthenticationAsync(KindleWebAuthenticationSnapshot snapshot,
        KindleWebAuthenticationAction action, string account, string secret, CancellationToken cancellationToken);
    Task ClearAuthenticationFieldsAsync(CancellationToken cancellationToken);
}
