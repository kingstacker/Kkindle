using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kkindle.Core;

namespace Kkindle.TestFixtures;

/// <summary>Fictional account responses for client protocol and UI behavior tests.</summary>
public sealed class MembershipHttpHandler : HttpMessageHandler
{
    public const string Password = "fictional-membership-test-password";
    public AccountUser User { get; } = new(Guid.NewGuid(), "fictional_member");
    private readonly string _bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public Guid CurrentDeviceId { get; private set; }
    public Guid OtherDeviceId { get; } = Guid.NewGuid();
    public List<AccountDevice> Devices { get; } = [];
    public List<PurchaseOrder> Orders { get; } = [];
    public bool LifetimeFeatures { get; set; } = true;
    public bool Revoked { get; set; }
    public bool Offline { get; set; }
    public int SessionChecks { get; private set; }
    public int FeatureChecks { get; private set; }
    public Guid? LastKickedDevice { get; private set; }
    public PurchaseRequest? LastPurchase { get; private set; }
    public Func<DeviceFeatureStatus, DeviceFeatureStatus>? TransformStatus { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (Offline) throw new HttpRequestException("fictional network outage");
        var region = request.Headers.GetValues("X-Kkindle-Region").Single() == "china" ? AccountRegion.China : AccountRegion.International;
        var action = request.RequestUri!.AbsolutePath.Split("/api/accounts/")[1];
        if (action is "login" or "register")
        {
            var credentials = await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.AccountCredentials, token);
            if (credentials!.Username != User.Username || credentials.Password != Password) return new(HttpStatusCode.Unauthorized);
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
                if (!LifetimeFeatures)
                {
                    var denied = new HttpResponseMessage(HttpStatusCode.PaymentRequired);
                    denied.Headers.Add("X-Kkindle-Error", "membership_required");
                    return denied;
                }
            }
            var active = Devices.Count(device => device.State == "active");
            var member = new AccountMembership(User.Id, region, LifetimeFeatures ? "lifetime" : "free", LifetimeFeatures,
                null, 5, active, LifetimeFeatures ? active : 0, 256, 1024, 128, 896, 0, 512, 2048, null);
            var status = new DeviceFeatureStatus(member, Devices.ToArray(), CurrentDeviceId);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(TransformStatus?.Invoke(status) ?? status,
                AccountJsonContext.Default.DeviceFeatureStatus) };
        }
        if (action == "products") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new MembershipProduct[]
            { new("fictional-lifetime", "Fictional lifetime product", "lifetime", 1, "CNY", 0, 0, "Fictional local purchase; manual confirmation required.") },
            AccountJsonContext.Default.MembershipProductArray) };
        if (action == "orders" && request.Method == HttpMethod.Get) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(Orders.ToArray(), AccountJsonContext.Default.PurchaseOrderArray) };
        if (action == "orders" && request.Method == HttpMethod.Post)
        {
            LastPurchase = await request.Content!.ReadFromJsonAsync(AccountJsonContext.Default.PurchaseRequest, token);
            var order = new PurchaseOrder(Guid.NewGuid(), User.Id, LastPurchase!.ProductId, "Fictional lifetime product",
                "lifetime", 1, "CNY", 0, 0, "pending", Now, Now.AddDays(2), null);
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
}
