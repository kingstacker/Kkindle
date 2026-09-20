using Kkindle.Infrastructure;

namespace Kkindle.McpServer;

internal static class McpRootPath
{
    private const string RootArgument = "--root";
    private const string RootEnvironmentVariable = "KKINDLE_ROOT";

    public static string Resolve(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var configuredRoot = ReadRootArgument(args)
            ?? Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return Path.GetFullPath(configuredRoot.Trim());

        // Keep the same app-root.json convention as the desktop heads. When
        // the MCP server is deployed beside the desktop app, both processes
        // therefore resolve the same library and reader database.
        return AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory);
    }

    private static string? ReadRootArgument(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(RootArgument, StringComparison.OrdinalIgnoreCase))
                return index + 1 < args.Count ? args[index + 1] : throw new ArgumentException(
                    $"{RootArgument} requires a directory path.", nameof(args));

            var prefix = RootArgument + "=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return argument[prefix.Length..];
        }

        return null;
    }
}
