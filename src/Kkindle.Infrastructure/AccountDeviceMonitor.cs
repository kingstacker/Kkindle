using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Checks devices every 24 hours and at a known Plus expiry, across app restarts.</summary>
public sealed class AccountDeviceMonitor(AccountService accounts, AppPaths paths, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private Task? _worker;
    private string Checkpoint => Path.Combine(paths.Data, "device-check.json");

    public event EventHandler<AccountMembershipExpiredEventArgs>? MembershipExpired;
    public event EventHandler<AccountMembershipExpiringEventArgs>? MembershipExpiring;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _worker = RunAsync();
    }

    public async Task<bool> CheckIfDueAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var account = await accounts.GetStatusAsync(cancellationToken: token);
            if (!account.IsLoggedIn) return false;
            var device = await accounts.GetDeviceAsync(token);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{account.Region}|{account.ServerUrl}|{account.User!.Id}|{device.DeviceId}")));
            var checks = new Dictionary<string, DateTimeOffset>();
            if (File.Exists(Checkpoint) && new FileInfo(Checkpoint).Length <= 65536)
            {
                try
                {
                    checks = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Checkpoint, token),
                        DeviceCheckJsonContext.Default.DictionaryStringDateTimeOffset) ?? checks;
                }
                catch (JsonException) { }
            }
            var now = _clock.GetUtcNow();
            var activeExpiryKey = "plus-active-expiry:" + key;
            var expiryDue = checks.TryGetValue(activeExpiryKey, out var activeExpiry) && activeExpiry <= now;
            if (!expiryDue && checks.TryGetValue(key, out var previous) && previous <= now && now-previous < TimeSpan.FromDays(1)) return false;
            // Connection failures don't erase a valid local session or record a successful check.
            var checkedAccount = await accounts.GetStatusAsync(validateSession: true, cancellationToken: token);
            if (checkedAccount.Region != account.Region || checkedAccount.ServerUrl != account.ServerUrl
                || (checkedAccount.IsLoggedIn && checkedAccount.User?.Id != account.User?.Id)) return false;
            AccountMembershipExpiredEventArgs? notice = null;
            AccountMembershipExpiringEventArgs? expiring = null;
            if (checkedAccount.IsLoggedIn)
            {
                var status = await new DeviceMembershipService(accounts).RefreshAsync(token);
                now = _clock.GetUtcNow();
                if (status.Membership.Tier == "pro" && status.Membership.ProExpiresAt is { } nextExpiry && nextExpiry > now)
                {
                    checks[activeExpiryKey] = nextExpiry;
                    if (MemberEntitlements.PlusRenewalReminder(nextExpiry, now) is { } threshold)
                    {
                        var reminderKey = "plus-expiring:" + threshold + ":" + key;
                        if (!checks.TryGetValue(reminderKey, out var notified) || notified != nextExpiry)
                        {
                            // Remember this threshold only: a user who first opens the app
                            // at 6 days gets the 7-day notice without a burst of 30/7/1,
                            // and later 1-day notices still arrive on their own check.
                            checks[reminderKey] = nextExpiry;
                            expiring = new(nextExpiry, Math.Max(1, (int)Math.Ceiling((nextExpiry - now).TotalDays)),
                                MemberEntitlements.CloudRetentionEndsAt(nextExpiry, status.Membership.TrialExpiresAt),
                                MemberEntitlements.PlusRenewalMessage(nextExpiry, now, status.Membership.TrialExpiresAt));
                        }
                    }
                }
                else
                    checks.Remove(activeExpiryKey);
                if (status.Membership.ProExpiresAt is { } expiry && expiry <= now && status.Membership.Tier != "pro")
                {
                    var noticeKey = "plus-expiry:" + key;
                    if (!checks.TryGetValue(noticeKey, out var notifiedExpiry) || notifiedExpiry != expiry)
                    {
                        checks[noticeKey] = expiry;
                        notice = new(expiry, MemberEntitlements.CloudRetentionEndsAt(expiry, status.Membership.TrialExpiresAt),
                            MemberEntitlements.PlusExpiryMessage(expiry, now, status.Membership.TrialExpiresAt));
                    }
                }
            }
            checks[key] = now;
            checks = checks.OrderByDescending(item => item.Value).Take(64).ToDictionary(item => item.Key, item => item.Value);
            using var lease = await SettingsWriteLock.AcquireAsync(paths, token);
            var temporary = Checkpoint + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(checks,
                    DeviceCheckJsonContext.Default.DictionaryStringDateTimeOffset), token);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, Checkpoint, overwrite: true);
            }
            finally { File.Delete(temporary); }
            if (notice is not null) MembershipExpired?.Invoke(this, notice);
            if (expiring is not null) MembershipExpiring?.Invoke(this, expiring);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), _clock);
        try
        {
            do
            {
                try { await CheckIfDueAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        // The cancellation is observed by both the account HTTP request and the timer.
        _ = _worker;
    }
}

public sealed record AccountMembershipExpiredEventArgs(DateTimeOffset ExpiredAt, DateTimeOffset CloudRetentionEndsAt, string Message);

public sealed record AccountMembershipExpiringEventArgs(DateTimeOffset ExpiresAt, int DaysRemaining,
    DateTimeOffset CloudRetentionEndsAt, string Message);

[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
internal partial class DeviceCheckJsonContext : JsonSerializerContext;
