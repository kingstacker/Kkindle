using System.IO.Compression;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class BookFormatConversionServiceTests
{
    [Fact]
    public async Task ThrowsWhenSourceBookDoesNotExist()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var service = new BookFormatConversionService();
            var missing = Path.Combine(root, "missing.epub");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                service.ConvertAsync(missing, Path.Combine(root, "out.pdf")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsUnsupportedSourceOrTargetFormats()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var epub = Path.Combine(root, "book.epub");
            var text = Path.Combine(root, "book.txt");
            await File.WriteAllTextAsync(epub, "epub");
            await File.WriteAllTextAsync(text, "text");
            var service = new BookFormatConversionService();

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ConvertAsync(epub, Path.Combine(root, "out.txt")));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.ConvertAsync(text, Path.Combine(root, "out.epub")));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsSameSourceAndDestinationPath()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.epub");
            await File.WriteAllTextAsync(source, "epub");
            var service = new BookFormatConversionService();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConvertAsync(source, source));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public async Task RejectsExistingDestinationFile()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "book.epub");
            var destination = Path.Combine(root, "book.pdf");
            await File.WriteAllTextAsync(source, "epub");
            await File.WriteAllTextAsync(destination, "occupied");
            var service = new BookFormatConversionService();

            await Assert.ThrowsAsync<IOException>(() =>
                service.ConvertAsync(source, destination));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void LocateExecutableHonorsKkindleCalibreConvertOverride()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var fakeExecutable = Path.Combine(root, "ebook-convert.exe");
            File.WriteAllText(fakeExecutable, "fake");
            var previous = Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT");
            Environment.SetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT", fakeExecutable);
            try
            {
                Assert.Equal(
                    Path.GetFullPath(fakeExecutable),
                    BookFormatConversionService.LocateExecutable());
            }
            finally
            {
                Environment.SetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT", previous);
            }
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("Linux", "calibre", "ebook-convert")]
    [InlineData("MacOS", "calibre", "ebook-convert")]
    [InlineData("Windows", "calibre.exe", "ebook-convert.exe")]
    public void CalibreLocatorRepairsConfiguredGuiLauncherPath(
        string operatingSystemName,
        string launcherName,
        string converterName)
    {
        var operatingSystem = Enum.Parse<DesktopOperatingSystem>(operatingSystemName);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var launcher = Path.Combine(root, launcherName);
            var converter = Path.Combine(root, converterName);
            File.WriteAllText(launcher, "fake calibre launcher");
            File.WriteAllText(converter, "fake ebook-convert");

            Assert.Equal(
                Path.GetFullPath(converter),
                CalibreExecutableLocator.Locate(root, launcher, null, operatingSystem));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("Linux", "ebook-convert")]
    [InlineData("MacOS", "ebook-convert")]
    [InlineData("Windows", "ebook-convert.exe")]
    public void CalibreLocatorAcceptsUserSelectedInstallationDirectory(string operatingSystemName, string executableName)
    {
        var operatingSystem = Enum.Parse<DesktopOperatingSystem>(operatingSystemName);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var executable = Path.Combine(root, executableName);
            File.WriteAllText(executable, "fake");
            Assert.Equal(
                Path.GetFullPath(executable),
                CalibreExecutableLocator.Locate(root, root, null, operatingSystem));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("Linux", "ebook-convert")]
    [InlineData("MacOS", "ebook-convert")]
    [InlineData("Windows", "ebook-convert.exe")]
    public void CalibreLocatorUsesPlatformExecutableName(string operatingSystemName, string executableName)
    {
        var operatingSystem = Enum.Parse<DesktopOperatingSystem>(operatingSystemName);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var pathDirectory = Path.Combine(root, "path-entry");
            Directory.CreateDirectory(pathDirectory);
            var executable = Path.Combine(pathDirectory, executableName);
            File.WriteAllText(executable, "fake");
            Assert.Equal(
                Path.GetFullPath(executable),
                CalibreExecutableLocator.Locate(root, null, pathDirectory, operatingSystem));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("Linux", "ebook-convert")]
    [InlineData("MacOS", "ebook-convert")]
    [InlineData("Windows", "ebook-convert.exe")]
    public void CalibreLocatorFindsApplicationLocalInstallation(string operatingSystemName, string executableName)
    {
        var operatingSystem = Enum.Parse<DesktopOperatingSystem>(operatingSystemName);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var calibreDirectory = Path.Combine(root, "Calibre");
            Directory.CreateDirectory(calibreDirectory);
            var executable = Path.Combine(calibreDirectory, executableName);
            File.WriteAllText(executable, "fake");
            Assert.Equal(
                Path.GetFullPath(executable),
                CalibreExecutableLocator.Locate(root, null, null, operatingSystem));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("Linux", "calibre-bin/ebook-convert")]
    [InlineData("MacOS", "Applications/calibre.app/Contents/MacOS/ebook-convert")]
    public void CalibreLocatorFindsPerUserAutomaticInstallation(string operatingSystemName, string relativePath)
    {
        var operatingSystem = Enum.Parse<DesktopOperatingSystem>(operatingSystemName);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var executable = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fake");
            Assert.Equal(
                Path.GetFullPath(executable),
                CalibreExecutableLocator.Locate(root, null, null, operatingSystem, userProfile: root));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void KfxPluginValidationAcceptsExpectedCalibrePluginStructure()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "KFX Input.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "plugin-import-name-kfx_input.txt", "kfx_input");
                TestHelpers.AddZipEntry(archive, "__init__.py", "name = 'KFX Input'");
            }
            CalibreSetupService.ValidateKfxPluginPackage(path);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void KfxPluginValidationUsesInitializerNextToImportMarker()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "KFX Input.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                TestHelpers.AddZipEntry(archive, "dependencies/__init__.py", "name = 'Dependency'");
                TestHelpers.AddZipEntry(archive, "plugin-import-name-kfx_input.txt", "kfx_input");
                TestHelpers.AddZipEntry(archive, "__init__.py", "name = 'KFX Input'");
            }

            CalibreSetupService.ValidateKfxPluginPackage(path);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void KfxPluginValidationRejectsUnrelatedZip()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "plugin.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                TestHelpers.AddZipEntry(archive, "__init__.py", "name = 'Something Else'");
            Assert.Throws<InvalidDataException>(() => CalibreSetupService.ValidateKfxPluginPackage(path));
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData(0, "KFX Input (2.30.0)", "", true)]
    [InlineData(0, "", "KFX INPUT enabled", true)]
    [InlineData(0, "EPUB Input", "", false)]
    [InlineData(1, "KFX Input", "", false)]
    public void KfxPluginListingRequiresSuccessfulCommandAndPluginName(
        int exitCode,
        string output,
        string error,
        bool expected)
    {
        Assert.Equal(expected, CalibreSetupService.IsKfxInputListed(exitCode, output, error));
    }
}
