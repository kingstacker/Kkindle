using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views.Sheets;

/// <summary>System voice and speed used the next time the reader starts speaking.</summary>
public partial class SpeechSettingsSheet : UserControl
{
    private readonly TtsSettingsStore? _store;
    private readonly TtsSettings? _settings;
    private readonly Action? _saved;
    private readonly Action<string>? _error;

    public SpeechSettingsSheet() => InitializeComponent();

    public SpeechSettingsSheet(TtsSettingsStore store, TtsSettings settings,
        IReadOnlyList<TtsVoiceInfo> voices, Action saved, Action<string> error)
    {
        _store = store;
        _settings = settings;
        _saved = saved;
        _error = error;
        InitializeComponent();
        var items = new List<ComboBoxItem>
        {
            new() { Content = "系统默认（中文）", Tag = TtsOptions.DefaultVoice }
        };
        items.AddRange(voices.Select(voice => new ComboBoxItem
        {
            Content = string.IsNullOrWhiteSpace(voice.Culture) ? voice.Name : $"{voice.Name} · {voice.Culture}",
            Tag = voice.Id
        }));
        VoiceBox.ItemsSource = items;
        VoiceBox.SelectedItem = items.FirstOrDefault(item => (string?)item.Tag == settings.Voice) ?? items[0];
        VoiceHint.Text = voices.Count == 0
            ? "没有获取到语音列表，将使用设备默认语音。"
            : "语音由设备提供，可以在系统设置中下载更多语音。";
        SpeedSlider.Value = settings.Speed;
        SpeedText.Text = $"{settings.Speed:0.0}×";
    }

    private void OnSpeedChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (SpeedText is not null) SpeedText.Text = $"{e.NewValue:0.0}×";
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || _settings is null) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SaveButton.IsEnabled = false;
        try
        {
            if (VoiceBox.SelectedItem is ComboBoxItem { Tag: string voice }) _settings.Voice = voice;
            _settings.Speed = SpeedSlider.Value;
            await _store.SaveAsync(_settings);
            _saved?.Invoke();
        }
        catch (Exception exception)
        {
            _error?.Invoke("保存朗读设置失败：" + UiText.Localize(exception.Message));
            SaveButton.IsEnabled = true;
        }
    }
}
