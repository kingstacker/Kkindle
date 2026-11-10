using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.TestFixtures;

/// <summary>Fictional account responses for client protocol and UI behavior tests.</summary>
public sealed class MembershipHttpHandler : HttpMessageHandler
{
    public const string Password = "fictional-membership-test-password";
    public AccountUser User { get; private set; } = new(Guid.NewGuid(), "fictional_member", "member@example.invalid");
    private string _password = Password;
    private readonly string _bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public Guid CurrentDeviceId { get; private set; }
    public Guid OtherDeviceId { get; } = Guid.NewGuid();
    public List<AccountDevice> Devices { get; } = [];
    public List<PurchaseOrder> Orders { get; } = [];
    public bool LifetimeFeatures { get; set; } = true;
    public DateTimeOffset? ProExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public bool Offline { get; set; }
    public int SessionChecks { get; private set; }
    public int FeatureChecks { get; private set; }
    public Guid? LastKickedDevice { get; private set; }
    public PurchaseRequest? LastPurchase { get; private set; }
    public ActivationCodeRedeemRequest? LastActivationRequest { get; private set; }
    public int ActivationAttempts { get; private set; }
    public string? ActivationFailureCode { get; set; }
    public bool LoseNextActivationResponse { get; set; }
    public Func<ActivationCodeRedemption, ActivationCodeRedemption>? TransformRedemption { get; set; }
    public Dictionary<string, string> ActivationCodes { get; } = new(StringComparer.OrdinalIgnoreCase)
    { ["fictionalpluscode"] = "pro", ["fictionallifetimecode"] = "lifetime" };
    private readonly Dictionary<string, (Guid Id, Guid Request, string Kind, DateTimeOffset At)> _redemptions = new(StringComparer.OrdinalIgnoreCase);
    public Func<DeviceFeatureStatus, DeviceFeatureStatus>? TransformStatus { get; set; }
    public bool ProductsUnavailable { get; set; }
    public bool AccountClosed { get; private set; }
    public string UnavailableUsername { get; set; } = "taken_reader";
    public MembershipProduct[] Products { get; set; } =
    [
        new("fictional-lifetime", "Fictional lifetime product", "lifetime", 8800, "CNY", 0, 0,
            "Fictional local purchase; manual confirmation required.")
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Offline) throw new HttpRequestException("fictional network outage");
        var region = request.Headers.GetValues("X-Kkindle-Region").Single() == "china" ? AccountRegion.China : AccountRegion.International;
        var action = request.RequestUri!.AbsolutePath.Split("/api/accounts/")[1];
        if (action is "login" or "register")
        {
            var credentials = await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountCredentials, token);
            if (action == "register" && (!AccountValidation.IsEmail(credentials!.Email) || credentials.Password != credentials.ConfirmPassword))
                return new(HttpStatusCode.BadRequest);
            if (!(credentials!.Username.Equals(User.Username, StringComparison.OrdinalIgnoreCase)
                || credentials.Username.Equals(User.Email, StringComparison.OrdinalIgnoreCase)) || credentials.Password != _password) return new(HttpStatusCode.Unauthorized);
            if (action == "register") User = User with { Email = credentials.Email!.Trim() };
            CurrentDeviceId = credentials.DeviceId;
            Revoked = false;
            Devices.Clear();
            Devices.Add(new(CurrentDeviceId, credentials.DeviceName, credentials.Platform, "active", Now, Now));
            Devices.Add(new(OtherDeviceId, "Fictional second device", "iOS", "active", Now, Now));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSessionResponse
                { Region = region, User = User, Token = _bearer, ExpiresAt = Now.AddDays(30) }, AccountJsonContext.Default.AccountSessionResponse) };
        }
        if (action == "session") SessionChecks++;
        if (request.Headers.Authorization?.Parameter != _bearer || Revoked) return new(HttpStatusCode.Unauthorized);
        if (action == "session") return new(HttpStatusCode.OK) { Content = JsonContent.Create(
            new AccountSessionInfo(User, Now.AddDays(30), region), AccountJsonContext.Default.AccountSessionInfo) };
        if (action == "logout") { Revoked = true; return new(HttpStatusCode.NoContent); }
        if (action == "profile")
        {
            var profile = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountProfileUpdate, token))!;
            if (profile.CurrentPassword != _password) return Error(HttpStatusCode.Forbidden, "current_password_incorrect");
            if (profile.Username == UnavailableUsername) return Error(HttpStatusCode.Conflict, "username_exists");
            User = User with { Username = profile.Username };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(User, AccountJsonContext.Default.AccountUser) };
        }
        if (action == "password")
        {
            var change = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountPasswordChange, token))!;
            if (change.CurrentPassword != _password) return Error(HttpStatusCode.Forbidden, "current_password_incorrect");
            _password = change.NewPassword;
            for (var index = 0; index < Devices.Count; index++)
                if (Devices[index].DeviceId != CurrentDeviceId) Devices[index] = Devices[index] with { State = "signed_out" };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AccountSessionResponse
                { Region = region, User = User, Token = _bearer, ExpiresAt = Now.AddDays(30) }, AccountJsonContext.Default.AccountSessionResponse) };
        }
        if (action == "close")
        {
            var closure = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountClosureRequest, token))!;
            if (closure.Confirmation != "注销账号") return Error(HttpStatusCode.BadRequest, "confirmation_required");
            if (closure.CurrentPassword != _password) return Error(HttpStatusCode.Forbidden, "current_password_incorrect");
            AccountClosed = true; Revoked = true;
            return new(HttpStatusCode.Accepted);
        }
        if (action == "activation-codes/redeem")
        {
            ActivationAttempts++;
            LastActivationRequest = (await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.ActivationCodeRedeemRequest, token))!;
            var code = LastActivationRequest.Code.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            if (ActivationFailureCode is { } failure) return Error(HttpStatusCode.Conflict, failure);
            if (!ActivationCodes.TryGetValue(code, out var kind)) return Error(HttpStatusCode.BadRequest, "invalid_activation_code");
            var alreadyRedeemed = _redemptions.TryGetValue(code, out var redemption);
            if (!alreadyRedeemed)
            {
                if (_redemptions.Values.Any(item => item.Request == LastActivationRequest.RequestId)) return Error(HttpStatusCode.Conflict, "activation_request_conflict");
                if (kind == "pro")
                {
                    if (!MemberEntitlements.CanExtendPlus(Status(region).Membership, 365, Now)) return Error(HttpStatusCode.Conflict, "membership_limit");
                    ProExpiresAt = (ProExpiresAt > Now ? ProExpiresAt.Value : Now).AddDays(365);
                }
                else
                {
                    if (LifetimeFeatures) return Error(HttpStatusCode.Conflict, "lifetime_already_active");
                    LifetimeFeatures = true;
                }
                redemption = (Guid.NewGuid(), LastActivationRequest.RequestId, kind, Now);
                _redemptions.Add(code, redemption);
            }
            else if (redemption.Request != LastActivationRequest.RequestId) return Error(HttpStatusCode.Conflict, "activation_already_used");
            if (LoseNextActivationResponse) { LoseNextActivationResponse = false; throw new HttpRequestException("fictional lost activation response"); }
            var reply = new ActivationCodeRedemption(redemption.Id, kind, kind == "pro" ? 1 : 0, redemption.At, Status(region), alreadyRedeemed);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(TransformRedemption?.Invoke(reply) ?? reply, AccountJsonContext.Default.ActivationCodeRedemption) };
        }
        if (action.StartsWith("devices/", StringComparison.Ordinal))
        {
            var deviceId = Guid.Parse(action.Split('/')[1]);
            if (action.EndsWith("/kick", StringComparison.Ordinal))
            {
                LastKickedDevice = deviceId;
                var index = Devices.FindIndex(device => device.DeviceId == deviceId);
                if (index < 0) return new(HttpStatusCode.NotFound);
                if (Devices[index].State == "blocked")
                {
                    var blocked = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    blocked.Headers.Add("X-Kkindle-Error", "device_blocked");
                    return blocked;
                }
                Devices[index] = Devices[index] with { State = "signed_out" };
                if (deviceId == CurrentDeviceId) Revoked = true;
                return new(HttpStatusCode.NoContent);
            }
            if (action.EndsWith("/features", StringComparison.Ordinal))
            {
                FeatureChecks++;
                if (!LifetimeFeatures && !(ProExpiresAt > Now))
                {
                    var denied = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
                    denied.Headers.Add("X-Kkindle-Error", "membership_required");
                    return denied;
                }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Status(region),
                AccountJsonContext.Default.DeviceFeatureStatus) };
        }
        if (action == "products") return ProductsUnavailable ? new(HttpStatusCode.ServiceUnavailable)
            : new(HttpStatusCode.OK) { Content = JsonContent.Create(Products, AccountJsonContext.Default.MembershipProductArray) };
        if (action == "orders" && request.Method == HttpMethod.Get) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(Orders.ToArray(), AccountJsonContext.Default.PurchaseOrderArray) };
        if (action == "orders" && request.Method == HttpMethod.Post)
        {
            LastPurchase = await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.PurchaseRequest, token);
            var product = Products.FirstOrDefault(item => item.Id == LastPurchase!.ProductId);
            if (product is null) return Error(HttpStatusCode.BadRequest, "invalid_product");
            if (product.Kind == "pro" && !MemberEntitlements.CanExtendPlus(Status(region).Membership, product.DurationDays, Now))
                return Error(HttpStatusCode.Conflict, "membership_limit");
            var order = new PurchaseOrder(Guid.NewGuid(), User.Id, LastPurchase!.ProductId, product.Name,
                product.Kind, product.PriceMinor, product.Currency, product.DurationDays, product.Bytes, "pending", Now, Now.AddDays(2), null);
            Orders.Insert(0, order);
            return new(HttpStatusCode.Created) { Content = JsonContent.Create(order, AccountJsonContext.Default.PurchaseOrder) };
        }
        if (action.StartsWith("orders/", StringComparison.Ordinal) && action.EndsWith("/cancel", StringComparison.Ordinal))
        {
            var id = Guid.Parse(action.Split('/')[1]);
            var index = Orders.FindIndex(order => order.Id == id);
            if (index < 0) return new(HttpStatusCode.NotFound);
            Orders[index] = Orders[index] with { State = "cancelled" };
            return new(HttpStatusCode.NoContent);
        }
        return new(HttpStatusCode.NotFound);
    }

    private DeviceFeatureStatus Status(AccountRegion region)
    {
        var active = Devices.Count(device => device.State == "active");
        var plus = ProExpiresAt > Now;
        var features = LifetimeFeatures || plus;
        var member = new AccountMembership(User.Id, region, plus ? "pro" : LifetimeFeatures ? "lifetime" : "free", features,
            ProExpiresAt, MemberEntitlements.DeviceLimit, active, features ? active : 0, 256, 1024, 128, plus ? 150L * 1024 * 1024 * 1024 : 0, 0,
            512, plus ? 10L * 1024 * 1024 * 1024 : 0, null, plus ? 150L * 1024 * 1024 * 1024 : 0, 0,
            plus ? ProExpiresAt!.Value.AddDays(-365) : null, plus ? ProExpiresAt : null, Now.AddYears(5),
            Orders.Where(order => order.Kind == "pro" && order.State == "pending" && order.ExpiresAt > Now).Sum(order => order.DurationDays));
        var status = new DeviceFeatureStatus(member, Devices.ToArray(), CurrentDeviceId);
        return TransformStatus?.Invoke(status) ?? status;
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code)
    {
        var reply = new HttpResponseMessage(status);
        reply.Headers.Add("X-Kkindle-Error", code);
        return reply;
    }
}
