using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.MacOS;

/// <summary>
/// Installs a downloaded <c>.tar.gz</c> update without leaving Kkindle.
/// The app is distributed as a <c>Kkindle.app</c> bundle, so the update is
/// applied by a detached helper script: it waits for this process to exit,
/// extracts the archive, swaps the bundle in place and reopens the app.
/// Installation is only offered when the bundle directory is writable by the
/// current user; otherwise the release page remains the fallback.
/// </summary>
public sealed class MacOSAppUpdateInstaller : IAppUpdateInstaller
{
    private const string BundleName = "Kkindle.app";

    private static readonly Lazy<string?> BundlePathValue = new(() => FindBundlePath(AppContext.BaseDirectory));

    public static string? BundlePath => BundlePathValue.Value;

    public bool CanInstall
    {
        get
        {
            var bundle = BundlePath;
            if (bundle is null || !Directory.Exists(bundle)) return false;
            // App Translocation runs the bundle from a read-only random path.
            if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal)) return false;
            var parent = Path.GetDirectoryName(bundle);
            return parent is not null && Directory.Exists(parent) && IsWritableDirectory(parent);
        }
    }

    public string UnavailableReason =>
        "当前不是从可写的 Kkindle.app 应用包运行，暂不支持应用内安装。";

    public string GetPackageAssetName(string version)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("版本号不能用于生成安装包文件名。", nameof(version));
        return $"Kkindle-{version}-{RuntimeIdentifier}.tar.gz";
    }

    public void LaunchInstaller(string packagePath)
    {
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("找不到已下载的 Kkindle 更新包。", fullPath);
        if (!Path.GetFileName(fullPath).EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kkindle 更新包格式不正确。");
        var bundle = BundlePath;
        if (!CanInstall || bundle is null) throw new InvalidOperationException(UnavailableReason);

        // The helper lives outside the bundle so the bundle can be replaced
        // while the script still runs. Every path is passed as an argument;
        // the script itself is a constant and never interpolates them.
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"kkindle-update-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath, HelperScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var info = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(scriptPath);
        info.ArgumentList.Add(fullPath);
        info.ArgumentList.Add(bundle);
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(fullPath + ".install.log");

        using var process = Process.Start(info);
        if (process is null) throw new InvalidOperationException("无法启动 Kkindle 更新程序。");
    }

    /// <summary>
    /// Resolves the enclosing <c>.app</c> bundle from the executable directory
    /// (<c>Kkindle.app/Contents/MacOS</c>). Returns null for development builds.
    /// </summary>
    internal static string? FindBundlePath(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        var directory = Path.GetFullPath(baseDirectory);
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(directory); depth++)
        {
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
            if (string.Equals(name, BundleName, StringComparison.OrdinalIgnoreCase))
                return directory.TrimEnd(Path.DirectorySeparatorChar);
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    private static string RuntimeIdentifier =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

    private static bool IsWritableDirectory(string path)
    {
        var probe = Path.Combine(path, $".kkindle-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal const string HelperScript = """
        #!/bin/sh
        # Kkindle updater: archive, target bundle, parent pid, log.
        archive=$1
        target=$2
        parent_pid=$3
        log=$4
        exec >>"$log" 2>&1
        echo "[$(date '+%Y-%m-%d %H:%M:%S')] updating $target from $archive"
        # The app shuts down immediately after launching this script. Give up
        # (without touching the installed bundle) if it is still running after
        # two minutes, e.g. because a quit prompt was cancelled.
        waited=0
        while kill -0 "$parent_pid" 2>/dev/null; do
          if [ "$waited" -ge 600 ]; then
            echo "Kkindle did not exit within 120 seconds; update cancelled"
            exit 1
          fi
          sleep 0.2
          waited=$((waited + 1))
        done
        # Stage next to the installed bundle so the final swap is a same-volume
        # rename. A cross-volume mv copies file by file and can fail halfway.
        stage=$(mktemp -d "$(dirname "$target")/.kkindle-update.XXXXXX" 2>/dev/null) \
          || stage=$(mktemp -d "${TMPDIR:-/tmp}/kkindle-update.XXXXXX") || exit 1
        cleanup() { rm -rf "$stage"; }
        trap cleanup EXIT

        # Never let a tampered archive write outside the staging directory.
        # The updater only accepts a bundle at the archive root.
        members="$stage/archive-members.txt"
        if ! /usr/bin/tar -tzf "$archive" > "$members"; then
          echo "failed to list $archive"
          exit 1
        fi
        if [ ! -s "$members" ]; then
          echo "the archive is empty"
          exit 1
        fi
        while IFS= read -r member; do
          case "$member" in
            Kkindle.app|Kkindle.app/*|MACOS-README.md) ;;
            *)
              echo "unsafe archive member: $member"
              exit 1
              ;;
          esac
          case "/$member/" in
            */../*)
              echo "unsafe archive member: $member"
              exit 1
              ;;
          esac
        done < "$members"
        if /usr/bin/tar -tvzf "$archive" | grep -Eq '^[lh]'; then
          echo "the archive contains symbolic or hard links"
          exit 1
        fi

        if ! /usr/bin/tar -xzf "$archive" -C "$stage"; then
          echo "failed to extract $archive"
          exit 1
        fi
        if [ ! -d "$stage/Kkindle.app" ] || [ ! -x "$stage/Kkindle.app/Contents/MacOS/Kkindle" ]; then
          echo "the archive does not contain a runnable Kkindle.app"
          exit 1
        fi

        # Reject an update whose bundle signature is broken before replacing
        # the working copy. Ad-hoc signed builds still pass this check.
        if ! /usr/bin/codesign --verify --deep --strict "$stage/Kkindle.app" >/dev/null 2>&1; then
          echo "the downloaded bundle has an invalid code signature"
          exit 1
        fi

        # External MCP clients keep the bundled server running; stop it before
        # the bundle directory is replaced. Match the bundle path literally:
        # pkill -f treats characters such as "." or "+" in the path as a regex.
        /bin/ps -axo pid=,command= | while read -r pid command; do
          case "$command" in
            "$target"/Contents/mcp/Kkindle.McpServer*|"$target"/Contents/*/mcp/Kkindle.McpServer*)
              kill "$pid" 2>/dev/null || true
              ;;
          esac
        done
        sleep 0.5

        backup="$target.kkindle-old"
        rm -rf "$backup"
        if ! mv "$target" "$backup"; then
          echo "failed to move the current bundle aside"
          exit 1
        fi
        if ! mv "$stage/Kkindle.app" "$target"; then
          echo "failed to install the new bundle, restoring the previous version"
          # A partial copy may exist; without removing it, mv would move the
          # backup *into* it and leave neither version runnable.
          /bin/rm -rf "$target"
          mv "$backup" "$target"
          exit 1
        fi
        if ! /usr/bin/codesign --verify --deep --strict "$target" >/dev/null 2>&1; then
          echo "the installed bundle failed signature verification, restoring the previous version"
          /bin/rm -rf "$target"
          mv "$backup" "$target"
          exit 1
        fi
        /bin/chmod -R u+rwX,go+rX "$target" 2>/dev/null
        /usr/bin/xattr -dr com.apple.quarantine "$target" >/dev/null 2>&1
        echo "installed $target, relaunching"
        if ! /usr/bin/open "$target"; then
          echo "failed to relaunch $target, restoring the previous version"
          /bin/rm -rf "$target"
          mv "$backup" "$target"
          /usr/bin/open "$target" >/dev/null 2>&1
          exit 1
        fi
        /bin/rm -rf "$backup"
        /bin/rm -f "$0"
        exit 0
        """;
}
