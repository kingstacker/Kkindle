using System.ComponentModel;
using System.Diagnostics;
using Kkindle.Platform.Common;

namespace Kkindle.Platform.Linux;

public sealed class LinuxSecretProtector : AesGcmSecretProtector
{
    private const string ApplicationAttribute = "Kkindle";
    private const string PurposeAttribute = "secret-protection-key";

    protected override byte[] GetOrCreateKey()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux secret protection can only run on Linux.");
        var lookup = RunSecretTool(
            ["lookup", "application", ApplicationAttribute, "purpose", PurposeAttribute],
            allowFailure: true);
        if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.Output))
            return ParseStoredKey(lookup.Output);
        if (lookup.ExitCode == 0 || !IsMissingSecret(lookup.Error))
            throw new InvalidOperationException(
                $"Unable to read the existing Kkindle key from Secret Service; no new key was created. {lookup.Error}");

        var key = CreateKey();
        var store = RunSecretTool(
            ["store", "--label=Kkindle secret protection key", "application", ApplicationAttribute, "purpose", PurposeAttribute],
            Convert.ToBase64String(key) + Environment.NewLine,
            allowFailure: true);
        if (store.ExitCode != 0)
        {
            var racedLookup = RunSecretTool(
                ["lookup", "application", ApplicationAttribute, "purpose", PurposeAttribute],
                allowFailure: true);
            if (racedLookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(racedLookup.Output))
                return ParseStoredKey(racedLookup.Output);
            throw new InvalidOperationException($"Unable to store the Kkindle key in Secret Service: {store.Error}");
        }
        var verified = RunSecretTool(
            ["lookup", "application", ApplicationAttribute, "purpose", PurposeAttribute],
            allowFailure: true);
        if (verified.ExitCode == 0 && !string.IsNullOrWhiteSpace(verified.Output))
            return ParseStoredKey(verified.Output);
        throw new InvalidOperationException("Secret Service did not retain the generated Kkindle key.");
    }

    private static bool IsMissingSecret(string error) =>
        string.IsNullOrWhiteSpace(error)
        || error.Contains("no such secret", StringComparison.OrdinalIgnoreCase)
        || error.Contains("no matching secret", StringComparison.OrdinalIgnoreCase)
        || error.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static CommandResult RunSecretTool(IReadOnlyList<string> arguments, string? standardInput = null, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "secret-tool",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start secret-tool.");
            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(outputTask, errorTask);
            var result = new CommandResult(process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
            if (!allowFailure && result.ExitCode != 0)
                throw new InvalidOperationException($"secret-tool failed: {result.Error}");
            return result;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Kkindle requires secret-tool and an available Secret Service keyring on Linux.", exception);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
