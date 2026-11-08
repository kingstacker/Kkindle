using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kkindle.Infrastructure;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle.PluginTool;

/// <summary>
/// kkplugin — validate, pack, sign and index Kkindle plugins.
///
///   kkplugin validate &lt;folder|file.kkplugin&gt;
///   kkplugin pack &lt;folder&gt; [-o dir] [--key key.pem] [--publisher name]
///   kkplugin sign &lt;file.kkplugin&gt; --key key.pem [--publisher name]
///   kkplugin verify &lt;file.kkplugin&gt;
///   kkplugin keygen [-o dir]
///   kkplugin index &lt;packages-dir&gt; --base-url https://host/plugins/ [-o index.json]
/// </summary>
internal static class Program
{
    private static readonly string[] ExcludedNames = [".DS_Store", "Thumbs.db", ".git", "node_modules", "dist"];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // The tool is not the app: never refuse a package for minAppVersion,
        // only report it.
        PluginHostInfo.AppVersion = new Version(int.MaxValue, 0, 0);
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "validate" => Validate(options),
                "pack" => Pack(options),
                "sign" => Sign(options),
                "verify" => Verify(options),
                "keygen" => Keygen(options),
                "index" => Index(options),
                _ => Fail($"未知命令“{args[0]}”。运行 kkplugin help 查看用法。")
            };
        }
        catch (Exception exception) when (exception is PluginPackageException or IOException
            or UnauthorizedAccessException or ArgumentException or CryptographicException or JsonException
            or InvalidDataException)
        {
            return Fail(exception.Message);
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        kkplugin — Kkindle 插件开发工具

          validate <文件夹|插件.kkplugin>      检查清单、文件和签名，列出权限与贡献点
          pack <文件夹> [-o 输出目录]           打包为 .kkplugin（自动排除 .DS_Store、.git 等）
               [--key 私钥.pem] [--publisher 名称]   打包后立即签名
          sign <插件.kkplugin> --key 私钥.pem [--publisher 名称]
          verify <插件.kkplugin>                只校验签名
          keygen [-o 目录]                      生成 ECDSA P-256 签名密钥（私钥请妥善保管）
          index <插件目录> --base-url https://…/plugins/ [-o index.json]
                                                为目录中的 .kkplugin 生成插件市场索引
        """);

    // ---- commands -------------------------------------------------------

    private static int Validate(Options options)
    {
        var target = options.RequirePositional(0, "插件文件夹或 .kkplugin 文件");
        var manager = new PluginManager(new AppPaths(CreateScratch()));
        PluginInstallPreview preview;
        if (Directory.Exists(target))
        {
            manager.InspectDirectory(target);
            // Folders are packed by the app before install; check that path too.
            var package = PackFolder(target, Path.GetTempPath(), quiet: true);
            try { preview = manager.Inspect(package); }
            finally { File.Delete(package); }
        }
        else
        {
            preview = manager.Inspect(target);
        }
        Describe(preview.Manifest, preview.Signature);
        Console.WriteLine();
        Console.WriteLine("✓ 插件有效");
        return 0;
    }

    private static int Pack(Options options)
    {
        var folder = options.RequirePositional(0, "插件文件夹");
        if (!File.Exists(Path.Combine(folder, PluginManifest.FileName)))
            return Fail($"{folder} 中没有 manifest.json。");
        var outputDirectory = options.Get("-o") ?? options.Get("--output") ?? ".";
        Directory.CreateDirectory(outputDirectory);
        var package = PackFolder(folder, outputDirectory, quiet: false);
        var manager = new PluginManager(new AppPaths(CreateScratch()));
        var preview = manager.Inspect(package);
        if (options.Get("--key") is { } keyPath)
        {
            var signature = PluginSignature.Sign(package, File.ReadAllText(keyPath), options.Get("--publisher"));
            Console.WriteLine($"签名：{DescribeTrust(signature)}");
        }
        Console.WriteLine($"已打包 {preview.Manifest.Id} {preview.Manifest.Version} → {Path.GetFullPath(package)}");
        Console.WriteLine($"大小 {new FileInfo(package).Length:N0} 字节，SHA-256 {Sha256(package)}");
        return 0;
    }

    private static int Sign(Options options)
    {
        var package = options.RequirePositional(0, ".kkplugin 文件");
        var keyPath = options.Get("--key") ?? throw new ArgumentException("缺少 --key 私钥.pem。");
        var signature = PluginSignature.Sign(package, File.ReadAllText(keyPath), options.Get("--publisher"));
        Console.WriteLine($"已签名 {package}：{DescribeTrust(signature)}");
        Console.WriteLine($"SHA-256 {Sha256(package)}");
        return 0;
    }

    private static int Verify(Options options)
    {
        var package = options.RequirePositional(0, ".kkplugin 文件");
        var manager = new PluginManager(new AppPaths(CreateScratch()));
        var preview = manager.Inspect(package);
        Console.WriteLine(DescribeTrust(preview.Signature));
        return 0;
    }

    private static int Keygen(Options options)
    {
        var directory = options.Get("-o") ?? options.Get("--output") ?? ".";
        Directory.CreateDirectory(directory);
        var (privateKey, publicKey, keyId) = PluginSignature.CreateKey();
        var privatePath = Path.Combine(directory, $"kkplugin-{keyId}.pem");
        if (File.Exists(privatePath)) return Fail($"{privatePath} 已存在。");
        File.WriteAllText(privatePath, privateKey);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(directory, $"kkplugin-{keyId}.pub"), publicKey + Environment.NewLine);
        Console.WriteLine($"私钥：{Path.GetFullPath(privatePath)}（不要提交到仓库，丢失后无法再以同一签名发布更新）");
        Console.WriteLine($"公钥：{publicKey}");
        Console.WriteLine($"密钥 ID：{keyId}");
        return 0;
    }

    private static int Index(Options options)
    {
        var directory = options.RequirePositional(0, "包含 .kkplugin 的目录");
        var baseUrl = options.Get("--base-url");
        var output = options.Get("-o") ?? options.Get("--output") ?? Path.Combine(directory, "index.json");
        var manager = new PluginManager(new AppPaths(CreateScratch()));
        var plugins = new JsonArray();
        foreach (var package in Directory.EnumerateFiles(directory, "*" + PluginPackage.Extension).Order(StringComparer.Ordinal))
        {
            var preview = manager.Inspect(package);
            var manifest = preview.Manifest;
            var fileName = Path.GetFileName(package);
            var i18n = new JsonObject();
            foreach (var (language, localization) in manifest.Localizations)
            {
                i18n[language] = new JsonObject
                {
                    ["name"] = localization.Name,
                    ["description"] = localization.Description
                };
            }
            plugins.Add(new JsonObject
            {
                ["id"] = manifest.Id,
                ["name"] = manifest.Name,
                ["version"] = manifest.Version,
                ["description"] = manifest.Description,
                ["author"] = manifest.Author,
                ["homepage"] = manifest.Homepage,
                ["minAppVersion"] = manifest.MinAppVersion?.ToString(3),
                ["trust"] = preview.Signature.Trust.ToString().ToLowerInvariant(),
                ["download"] = baseUrl is null ? fileName : new Uri(new Uri(EnsureSlash(baseUrl)), fileName).ToString(),
                ["sha256"] = Sha256(package),
                ["size"] = new FileInfo(package).Length,
                ["i18n"] = i18n.Count == 0 ? null : i18n
            });
            Console.WriteLine($"  {manifest.Id} {manifest.Version} ({DescribeTrust(preview.Signature)})");
        }
        var index = new JsonObject
        {
            ["format"] = 1,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["plugins"] = plugins
        };
        File.WriteAllText(output, index.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n");
        // Validate the result with the app's own parser.
        var entries = PluginCatalogService.Parse(File.ReadAllText(output), new Uri(EnsureSlash(baseUrl ?? "https://example.invalid/")));
        Console.WriteLine($"已写入 {output}（{entries.Count} 个插件）");
        return entries.Count == plugins.Count ? 0 : Fail("部分条目没有通过应用的索引校验。");
    }

    // ---- helpers --------------------------------------------------------

    private static string PackFolder(string folder, string outputDirectory, bool quiet)
    {
        var root = Path.GetFullPath(folder);
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var output = Path.Combine(Path.GetFullPath(outputDirectory), quiet ? $"kkplugin-{Guid.NewGuid():N}{PluginPackage.Extension}" : name + PluginPackage.Extension);
        if (File.Exists(output)) File.Delete(output);
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => ExcludedNames.Contains(part, StringComparer.OrdinalIgnoreCase))) continue;
            if (relative.EndsWith(PluginPackage.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (relative == PluginSignature.FileName) continue;
            archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        }
        return output;
    }

    private static void Describe(PluginManifest manifest, PluginSignatureInfo signature)
    {
        Console.WriteLine($"{manifest.Name}（{manifest.Id}）{manifest.Version}");
        if (!string.IsNullOrWhiteSpace(manifest.Description)) Console.WriteLine($"  {manifest.Description}");
        Console.WriteLine($"  接口版本 {PluginManifest.SupportedApiVersion}，最低应用版本 {manifest.MinAppVersion?.ToString(3) ?? "不限"}");
        Console.WriteLine($"  签名：{DescribeTrust(signature)}");
        var permissions = new List<string>();
        if (manifest.LibraryAccess != PluginLibraryAccess.None) permissions.Add("library:" + manifest.LibraryAccess.ToString().ToLowerInvariant());
        if (manifest.HasLibraryScopeAll) permissions.Add("libraryAll");
        if (manifest.CanImportLibrary) permissions.Add("import");
        if (manifest.AllowsTranslation) permissions.Add("translation");
        if (manifest.AllowsPinyin) permissions.Add("pinyin");
        if (manifest.CanReadContent) permissions.Add("content");
        if (manifest.CanReadBookFiles) permissions.Add("bookFiles");
        if (manifest.CanWriteReadingData) permissions.Add("readingWrite");
        if (manifest.UsesAi) permissions.Add("ai");
        if (manifest.UsesStorage) permissions.Add("storage");
        if (manifest.UsesSecrets) permissions.Add("secrets");
        if (manifest.NetworkOrigins.Count > 0)
            permissions.Add("network:" + string.Join(",", manifest.NetworkOrigins.Select(origin => (origin.IncludesSubdomains ? "*." : "") + origin.Host)));
        Console.WriteLine($"  权限：{(permissions.Count == 0 ? "无" : string.Join("  ", permissions))}");
        var contributions = new List<string>();
        contributions.AddRange(manifest.BookActions.Select(action => $"bookAction:{action.Id}"));
        contributions.AddRange(manifest.Commands.Select(command => $"command:{command.Id}"));
        contributions.AddRange(manifest.ReaderActions.Select(action => $"readerAction:{action.Id}"));
        contributions.AddRange(manifest.Dictionaries.Select(provider => $"dictionary:{provider.Id}"));
        contributions.AddRange(manifest.Translators.Select(provider => $"translator:{provider.Id}"));
        if (manifest.Background is { } background)
            contributions.Add($"background:{(background.IntervalMinutes > 0 ? background.IntervalMinutes + "min" : "startup")}");
        if (manifest.AppPage is not null) contributions.Add("app");
        if (manifest.SettingsPage is not null) contributions.Add("settings");
        Console.WriteLine($"  贡献点：{(contributions.Count == 0 ? "无" : string.Join("  ", contributions))}");
        if (manifest.Localizations.Count > 0)
            Console.WriteLine($"  语言：{string.Join(", ", manifest.Localizations.Keys)}");
    }

    private static string DescribeTrust(PluginSignatureInfo signature) => signature.Trust switch
    {
        PluginTrustLevel.Official => $"官方签名（{signature.KeyId}）",
        PluginTrustLevel.Signed => $"已签名 {signature.Publisher ?? ""}（{signature.KeyId}）".Replace(" （", "（"),
        _ => "未签名"
    };

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string EnsureSlash(string url) => url.EndsWith('/') ? url : url + "/";

    private static string CreateScratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "kkplugin-tool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        return path;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("✗ " + message);
        return 1;
    }

    private sealed class Options
    {
        private readonly List<string> _positional = [];
        private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index].StartsWith('-') && index + 1 < args.Length)
                    options._named[args[index]] = args[++index];
                else if (args[index].StartsWith('-'))
                    throw new ArgumentException($"参数 {args[index]} 缺少值。");
                else
                    options._positional.Add(args[index]);
            }
            return options;
        }

        public string? Get(string name) => _named.GetValueOrDefault(name);

        public string RequirePositional(int index, string description) =>
            index < _positional.Count ? _positional[index] : throw new ArgumentException($"缺少{description}。");
    }
}
