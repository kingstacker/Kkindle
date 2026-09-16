using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow
{
    private bool _softwareLogExportInProgress;

    private async void ExportSoftwareLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_softwareLogExportInProgress) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        _softwareLogExportInProgress = true;
        ExportSoftwareLogsButton.IsEnabled = false;
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("导出软件日志"),
                SuggestedFileName = T(
                    "Kkindle-软件日志-{0}.zip",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", UiText.CurrentCulture)),
                FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }]
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                path += ".zip";

            var status = T("正在整理软件日志…");
            AboutLogsStatusText.Text = status;
            SetTaskStatus(status);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Application version"] = ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly),
                ["Operating system"] = RuntimeInformation.OSDescription,
                ["Runtime"] = RuntimeInformation.FrameworkDescription,
                ["Process architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["UI language"] = UiText.CurrentLanguage,
                ["Avalonia"] = typeof(AvaloniaObject).Assembly.GetName().Version?.ToString() ?? "unknown"
            };
            await new SoftwareLogExportService().ExportAsync(
                _paths,
                path,
                metadata,
                _lifetimeCancellation.Token);

            var success = T("软件日志已导出到 {0}", path);
            AboutLogsStatusText.Text = success;
            SetTaskStatus(success);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var failure = T("软件日志导出失败：{0}", UiText.Localize(exception.Message));
            AboutLogsStatusText.Text = failure;
            SetTaskStatus(failure);
        }
        finally
        {
            _softwareLogExportInProgress = false;
            ExportSoftwareLogsButton.IsEnabled = true;
        }
    }
}
