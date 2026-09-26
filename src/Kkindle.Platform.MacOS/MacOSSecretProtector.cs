using System.ComponentModel;
using System.Diagnostics;
using Kkindle.Platform.Common;

namespace Kkindle.Platform.MacOS;

public sealed class MacOSSecretProtector : AesGcmSecretProtector
{
    private const string ServiceName = "Kkindle.SecretProtectionKey";

    protected override byte[] GetOrCreateKey()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS secret protection can only run on macOS.");
        var account = Environment.UserName;
        var lookup = RunSecurity(["find-generic-password", "-s", ServiceName, "-a", account, "-w"], allowFailure: true);
        if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.Output)) return ParseStoredKey(lookup.Output);
        if (lookup.ExitCode == 0 || !IsMissingSecret(lookup.Error))
            throw new InvalidOperationException(
                $"Unable to read the existing Kkindle key from Keychain; no new key was created. {lookup.Error}");

        var key = CreateKey();
        var command = "add-generic-password -s " + QuoteInteractiveArgument(ServiceName)
            + " -a " + QuoteInteractiveArgument(account)
            + " -w " + QuoteInteractiveArgument(Convert.ToBase64String(key));
        var store = RunSecurity(["-i"], command + Environment.NewLine, allowFailure: true);
        var verified = RunSecurity(
            ["find-generic-password", "-s", ServiceName, "-a", account, "-w"],
            allowFailure: true);
        if (verified.ExitCode == 0 && !string.IsNullOrWhiteSpace(verified.Output))
            return ParseStoredKey(verified.Output);
        if (store.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to store the Kkindle key in Keychain: {store.Error}");
        }
        throw new InvalidOperationException("Keychain did not retain the generated Kkindle key.");
    }

    private static bool IsMissingSecret(string error) =>
        string.IsNullOrWhiteSpace(error)
        || error.Contains("item could not be found", StringComparison.OrdinalIgnoreCase)
        || error.Contains("errsecitemnotfound", StringComparison.OrdinalIgnoreCase)
        || error.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string QuoteInteractiveArgument(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static CommandResult RunSecurity(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the macOS security tool.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }
            process.WaitForExit();
            Task.WaitAll(outputTask, errorTask);
            var result = new CommandResult(process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
            if (!allowFailure && result.ExitCode != 0)
                throw new InvalidOperationException($"The macOS security tool failed: {result.Error}");
            return result;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The macOS security tool is unavailable.", exception);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
