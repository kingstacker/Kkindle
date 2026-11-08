#:property PublishAot=false
// Run with the repository's .NET 10 SDK:
// dotnet run --file scripts/verify-third-party.cs -- --repository ROOT --assets ASSETS [--publish DIRECTORY]
// This checker is offline and has no third-party NuGet dependencies.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Use --repository ROOT --assets ASSETS [--publish DIRECTORY] [--resources MAP] [--archive APK --archive-prefix assets/] or --mtp-source DIRECTORY [--mtp-architecture ARCH --mtp-binaries DIRECTORY].");
        return 2;
    }
    options.Add(args[index], args[index + 1]);
}

var failures = new List<string>();
var repository = Path.GetFullPath(options.GetValueOrDefault("--repository", Directory.GetCurrentDirectory()));
var bundle = Path.Combine(repository, "src", "Kkindle.Infrastructure", "ThirdParty", "Notices");

try
{
    if (options.TryGetValue("--mtp-source", out var mtpSource))
    {
        VerifyMtpSource(Path.GetFullPath(mtpSource));
    }
    else
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(bundle, "manifest.json")));
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new ArgumentException("Unsupported license manifest schema.");
        var packages = manifest.RootElement.GetProperty("packages").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString() + "/" + p.GetProperty("version").GetString(),
                          p => p.Clone(), StringComparer.OrdinalIgnoreCase);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in manifest.RootElement.GetProperty("documents").EnumerateArray())
        {
            var relative = document.GetProperty("path").GetString()!;
            var original = SafePath(bundle, relative);
            VerifyHash(original, document.GetProperty("sha256").GetString()!);
            expected.Add("ThirdParty/Notices/" + relative, original);
        }
        foreach (var relative in new[] { "manifest.json", "fonts/NOTICE.md" })
            expected.Add("ThirdParty/Notices/" + relative, SafePath(bundle, relative));

        foreach (var package in packages.Values)
        {
            if (package.GetProperty("documents").GetArrayLength() == 0)
                failures.Add("No original license documents for " + package.GetProperty("id").GetString());
            foreach (var document in package.GetProperty("documents").EnumerateArray())
                if (!expected.ContainsKey("ThirdParty/Notices/" + document.GetString()))
                    failures.Add("Unregistered license document for " + package.GetProperty("id").GetString());
        }
        foreach (var font in manifest.RootElement.GetProperty("fonts").EnumerateArray())
        {
            RequirePackage(font.GetProperty("package").GetString() + "/" + font.GetProperty("packageVersion").GetString(), packages);
            if (font.GetProperty("documents").GetArrayLength() == 0)
                failures.Add("No original font license for " + font.GetProperty("name").GetString());
            foreach (var document in font.GetProperty("documents").EnumerateArray())
                if (!expected.ContainsKey("ThirdParty/Notices/" + document.GetString()))
                    failures.Add("Unregistered font license for " + font.GetProperty("name").GetString());
        }

        if (options.TryGetValue("--assets", out var assetsFile))
        {
            using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsFile));
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
                if (library.Value.GetProperty("type").GetString() == "package")
                    RequirePackage(library.Name, packages);
            // Match font attributions to the restored assemblies themselves,
            // so changing a package's embedded font cannot keep stale notices.
            foreach (var font in manifest.RootElement.GetProperty("fonts").EnumerateArray()
                .GroupBy(f => f.GetProperty("package").GetString() + "/" + f.GetProperty("packageVersion").GetString() + "/" + f.GetProperty("assembly").GetString())
                .Select(group => group.First()))
            {
                var key = font.GetProperty("package").GetString() + "/" + font.GetProperty("packageVersion").GetString();
                if (!assets.RootElement.GetProperty("libraries").TryGetProperty(key, out var library)) continue;
                var packagePath = library.GetProperty("path").GetString()!;
                var path = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
                    .Select(folder => SafePath(folder.Name, packagePath + "/" + font.GetProperty("assembly").GetString()))
                    .FirstOrDefault(File.Exists);
                if (path is null) failures.Add("Cannot inspect the restored font assembly for " + key);
                else VerifyHash(path, font.GetProperty("assemblySha256").GetString()!);
            }
        }

        expected.Add("LICENSE", Path.Combine(repository, "LICENSE"));
        expected.Add("THIRD-PARTY-NOTICES.md", Path.Combine(repository, "THIRD-PARTY-NOTICES.md"));
        var infrastructure = Path.Combine(repository, "src", "Kkindle.Infrastructure");
        foreach (var directory in new[] { "MdxParser", "KindleUnpack", "PDFium", "G2PW" })
        {
            var root = Path.Combine(infrastructure, "ThirdParty", directory);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                // G2PW's dictionary and MDX's C# sources are not license files.
                if (directory == "MdxParser" && extension is not (".md" or ".txt" or ".html")) continue;
                if (directory == "G2PW" && Path.GetFileName(file) is not ("NOTICE.md" or "LICENSE.txt")) continue;
                var relative = "ThirdParty/" + directory + "/" + Path.GetRelativePath(root, file).Replace('\\', '/');
                expected.Add(relative, file);
            }
        }
        // Enumeration alone would overlook a deleted source notice. These
        // incorporated components must always retain their original licenses.
        foreach (var required in new[] {
            "ThirdParty/MdxParser/NOTICE.md", "ThirdParty/MdxParser/LICENSE.txt",
            "ThirdParty/MdxParser/LICENSE.MiniLZO.txt", "ThirdParty/MdxParser/LICENSE.BouncyCastle.html",
            "ThirdParty/KindleUnpack/NOTICE.md", "ThirdParty/KindleUnpack/LICENSE.txt",
            "ThirdParty/PDFium/LICENSE", "ThirdParty/PDFium/NOTICE.md",
            "ThirdParty/G2PW/LICENSE.txt", "ThirdParty/G2PW/NOTICE.md" })
            if (!expected.ContainsKey(required)) failures.Add("Missing mandatory incorporated-source notice: " + required);

        if (options.ContainsKey("--resources"))
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repository, "src", "Kkindle.App", "Assets"), "*-LICENSE.txt", SearchOption.AllDirectories))
                expected.Add("Assets/" + Path.GetRelativePath(Path.Combine(repository, "src", "Kkindle.App", "Assets"), file).Replace('\\', '/'), file);
        }
        foreach (var item in expected)
            if (!File.Exists(item.Value)) failures.Add("Missing source license/notice: " + item.Value);

        if (options.TryGetValue("--resources", out var resourceMap))
        {
            var resources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(resourceMap))
            {
                var parts = line.Split('|', 2);
                if (parts.Length != 2 || string.IsNullOrEmpty(parts[1])) continue;
                resources[parts[1].Replace('\\', '/')] = parts[0];
            }
            foreach (var item in expected)
                if (!resources.TryGetValue(item.Key, out var resource)) failures.Add("Missing mobile license resource mapping: " + item.Key);
                else VerifyCopy(resource, item.Value);
        }

        if (options.TryGetValue("--archive", out var archiveFile))
        {
            using var archive = ZipFile.OpenRead(archiveFile);
            var prefix = options.GetValueOrDefault("--archive-prefix", "assets/");
            foreach (var item in expected)
            {
                var entry = archive.GetEntry(prefix + item.Key);
                if (entry is null) failures.Add("Missing packaged license resource: " + prefix + item.Key);
                else
                {
                    using var content = entry.Open();
                    if (!SHA256.HashData(content).SequenceEqual(SHA256.HashData(File.ReadAllBytes(item.Value))))
                        failures.Add("Packaged license checksum differs: " + entry.FullName);
                }
            }
        }

        if (options.TryGetValue("--publish", out var publishDirectory))
        {
            publishDirectory = Path.GetFullPath(publishDirectory);
            foreach (var item in expected)
                VerifyCopy(SafePath(publishDirectory, item.Key), item.Value);
            var depsFiles = Directory.GetFiles(publishDirectory, "*.deps.json");
            if (depsFiles.Length == 0) failures.Add("Published output has no dependency inventory (*.deps.json).");
            foreach (var depsFile in depsFiles)
            {
                using var deps = JsonDocument.Parse(File.ReadAllBytes(depsFile));
                foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
                {
                    var type = library.Value.GetProperty("type").GetString();
                    if (type is "package" or "runtimepack")
                        RequirePackage(library.Name.StartsWith("runtimepack.", StringComparison.OrdinalIgnoreCase)
                            ? library.Name["runtimepack.".Length..] : library.Name, packages);
                }
            }
            if (options.GetValueOrDefault("--configuration") == "Release" &&
                Directory.EnumerateFiles(publishDirectory, "AvaloniaUI.DiagnosticsSupport*.dll").Any())
                failures.Add("Development-only DiagnosticsSupport binaries unexpectedly appeared in Release output.");
        }
        Console.WriteLine($"Checked {packages.Count} documented package versions and {expected.Count} license/notice files.");
    }
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or ArgumentException)
{
    failures.Add(exception.Message);
}

foreach (var failure in failures) Console.Error.WriteLine("License verification failed: " + failure);
return failures.Count == 0 ? 0 : 1;

string SafePath(string root, string relative)
{
    var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new ArgumentException("License path escapes its bundle directory.");
    return full;
}

void RequirePackage(string key, Dictionary<string, JsonElement> packages)
{
    if (!packages.ContainsKey(key))
        failures.Add("Undocumented package version " + key + "; restore it and run scripts/update-third-party-notices.py, then review its original licenses.");
}

void VerifyHash(string file, string expected)
{
    if (!File.Exists(file))
    {
        failures.Add("Missing original document: " + file);
        return;
    }
    using var stream = File.OpenRead(file);
    var actual = Convert.ToHexString(SHA256.HashData(stream));
    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        failures.Add("Original document checksum differs: " + file);
}

void VerifyCopy(string copy, string original)
{
    if (!File.Exists(original))
    {
        failures.Add("Missing source license/notice: " + original);
        return;
    }
    VerifyHash(copy, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))));
}

void VerifyMtpSource(string directory)
{
    var checksums = Path.Combine(directory, "SHA256SUMS.txt");
    var files = new HashSet<string>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(checksums))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var split = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length != 2 || !Regex.IsMatch(split[0], "^[a-fA-F0-9]{64}$"))
            throw new ArgumentException("Invalid MTP source checksum entry.");
        var relative = split[1].TrimStart(' ', '*');
        VerifyHash(SafePath(directory, relative), split[0]);
        if (!files.Add(relative)) failures.Add("Duplicate MTP source checksum entry: " + relative);
    }
    var scriptFile = Path.Combine(directory, "build-macos-mtp-deps.sh");
    var script = File.ReadAllText(scriptFile);
    string Pinned(string name)
    {
        var match = Regex.Match(script, "(?m)^" + Regex.Escape(name) + "=\"([^\"]+)\"\\r?$");
        if (!match.Success) throw new ArgumentException("Missing pinned build input in MTP builder: " + name);
        return match.Groups[1].Value;
    }
    var usbVersion = Pinned("libusb_version");
    var mtpVersion = Pinned("libmtp_version");
    foreach (var required in new[] { $"libusb-{usbVersion}.tar.bz2", $"libmtp-{mtpVersion}.tar.gz",
                                     "libusb-COPYING", "libmtp-COPYING", "build-macos-mtp-deps.sh", "README.md" })
        if (!files.Contains(required)) failures.Add("Missing corresponding MTP source material: " + required);
    VerifyHash(Path.Combine(directory, $"libusb-{usbVersion}.tar.bz2"), Pinned("libusb_sha256"));
    VerifyHash(Path.Combine(directory, $"libmtp-{mtpVersion}.tar.gz"), Pinned("libmtp_sha256"));
    var metadataFiles = files.Where(f => f.StartsWith("BUILD-METADATA-", StringComparison.Ordinal) && f.EndsWith(".txt", StringComparison.Ordinal)).ToArray();
    if (metadataFiles.Length == 0) failures.Add("Missing MTP per-architecture build metadata.");
    foreach (var metadataFile in metadataFiles)
    {
        var metadata = File.ReadLines(SafePath(directory, metadataFile)).Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        var arch = metadata.GetValueOrDefault("architecture");
        if (arch is not ("arm64" or "x86_64") || metadataFile != "BUILD-METADATA-" + arch + ".txt")
            failures.Add("Invalid MTP architecture metadata: " + metadataFile);
        foreach (var pair in new Dictionary<string, string> {
            ["libusb.version"] = usbVersion, ["libmtp.version"] = mtpVersion,
            ["libusb.archive.sha256"] = Pinned("libusb_sha256"), ["libmtp.archive.sha256"] = Pinned("libmtp_sha256"),
            ["build_script.sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scriptFile))).ToLowerInvariant() })
            if (metadata.GetValueOrDefault(pair.Key) != pair.Value) failures.Add("MTP build input differs: " + metadataFile + ": " + pair.Key);
        if (options.TryGetValue("--mtp-binaries", out var binaryDirectory) && options.GetValueOrDefault("--mtp-architecture") == arch)
            foreach (var binary in new[] { "libusb-1.0.0.dylib", "libmtp.9.dylib" })
                VerifyHash(Path.Combine(binaryDirectory, binary), metadata[binary + ".sha256"]);
    }
    if (options.TryGetValue("--mtp-architecture", out var requiredArch) && !files.Contains("BUILD-METADATA-" + requiredArch + ".txt"))
        failures.Add("Missing MTP build metadata for " + requiredArch);
    if (options.ContainsKey("--mtp-binaries") && !options.ContainsKey("--mtp-architecture"))
        failures.Add("Checking MTP binaries requires --mtp-architecture.");
    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
        if (relative != "SHA256SUMS.txt" && !files.Contains(relative)) failures.Add("Unregistered MTP source material: " + relative);
    }
    Console.WriteLine($"Checked {files.Count} corresponding MTP source/build/license files.");
}
