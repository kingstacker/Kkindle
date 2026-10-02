using System.Reflection;
using System.Runtime.InteropServices;
using Kkindle.Platform.Windows;

namespace Kkindle.Tests.Windows;

public sealed class WpdFileNameTests
{
    [Theory]
    [InlineData("人月神话", "人月神话.azw3", "人月神话.azw3")]
    [InlineData("Downloads", "Downloads", "Downloads")]
    [InlineData("book.pdf", "", "book.pdf")]
    public void ResolvesActualFileNameWhenShellHidesExtensions(string displayName, string fileName, string expected)
    {
        Assert.Equal(expected, Resolve(new ShellItem(displayName, fileName)));
    }

    [Fact]
    public void FallsBackToDisplayNameWhenPropertyIsUnavailable()
    {
        Assert.Equal("book.azw3", Resolve(new ShellItem("book.azw3", null)));
    }

    private static string Resolve(ShellItem item)
    {
        var type = typeof(KindleDeviceService).Assembly.GetType("Kkindle.Platform.Windows.WpdKindleAccess")!;
        var method = type.GetMethod("GetItemFileName", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [item])!;
    }

    public sealed class ShellItem(string name, string? fileName)
    {
        public string Name => name;
        public string ExtendedProperty(string property)
        {
            Assert.Equal("System.FileName", property);
            return fileName ?? throw new COMException("Property unavailable");
        }
    }
}
