using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>At most one successful server device check per account every 24 hours, across app restarts.</summary>
public sealed class AccountDeviceMonitor(AccountService accounts, AppPaths paths, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _started;
    private Task? _worker;
    private string Checkpoint => Path.Combine(paths.Data, "device-check.json");

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
            if (checks.TryGetValue(key, out var previous) && previous <= now && now-previous < TimeSpan.FromDays(1)) return false;
            // Connection failures don't erase a valid local session or record a successful check.
            var checkedAccount = await accounts.GetStatusAsync(validateSession: true, cancellationToken: token);
            if (checkedAccount.Region != account.Region || checkedAccount.ServerUrl != account.ServerUrl
                || (checkedAccount.IsLoggedIn && checkedAccount.User?.Id != account.User?.Id)) return false;
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

[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
internal partial class DeviceCheckJsonContext : JsonSerializerContext;
