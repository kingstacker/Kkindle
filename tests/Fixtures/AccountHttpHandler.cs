using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.TestFixtures;

/// <summary>A deterministic account reply fixture for exercising UI controls.</summary>
public sealed class AccountHttpHandler : HttpMessageHandler
{
    public const string TestPassword = "fictional-ui-password";
    private readonly Dictionary<AccountRegion, FixtureSession> _sessions = new()
    { [AccountRegion.China] = new(), [AccountRegion.International] = new() };
    private readonly DateTimeOffset _expiry = DateTimeOffset.UtcNow.AddDays(30);
    public int CredentialRequests { get; private set; }
    public bool Revoked { get; private set; }
    public AccountRegion LastRequestRegion { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var region = request.Headers.GetValues("X-Kkindle-Region").Single() == "international" ? AccountRegion.International : AccountRegion.China;
        LastRequestRegion = region;
        var session = _sessions[region];
        var action = request.RequestUri!.Segments.Last();
        if (action is "register" or "login")
        {
            CredentialRequests++;
            var credentials = await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountCredentials, token);
            if (credentials!.Region != region) return new(HttpStatusCode.Forbidden);
            if (action == "register" && (!AccountValidation.IsEmail(credentials.Email) || credentials.Password != credentials.ConfirmPassword))
                return new(HttpStatusCode.BadRequest);
            if (!(credentials.Username.Equals(session.User.Username, StringComparison.OrdinalIgnoreCase)
                || session.User.Email is { } email && credentials.Username.Equals(email, StringComparison.OrdinalIgnoreCase)) || credentials.Password != TestPassword)
                return new(HttpStatusCode.Unauthorized);
            if (action == "register") session.User = session.User with { Email = credentials.Email };
            Revoked = false;
            session.Revoked = false;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSessionResponse
                { User = session.User, Token = session.Token, ExpiresAt = _expiry, Region = region }, AccountJsonContext.Default.AccountSessionResponse) };
        }
        if (request.Headers.Authorization?.Parameter != session.Token) return new(HttpStatusCode.Unauthorized);
        if (action == "logout") { Revoked = true; session.Revoked = true; return new(HttpStatusCode.NoContent); }
        if (action == "session") return session.Revoked ? new(HttpStatusCode.Unauthorized)
            : new(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSessionInfo(session.User, _expiry, region), AccountJsonContext.Default.AccountSessionInfo) };
        return new(HttpStatusCode.NotFound);
    }

    private sealed class FixtureSession
    {
        public AccountUser User { get; set; } = new(Guid.NewGuid(), "test_reader");
        public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public bool Revoked { get; set; }
    }
}
