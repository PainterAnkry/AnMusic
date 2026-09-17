using AnMusic.Android.Services;
using Microsoft.Maui.Controls.Shapes;

namespace AnMusic.Android;

/// <summary>
/// 均衡器页：开关 + 设备预设 + 各频段增益。
/// 频段数量与范围由设备决定，所以界面元素全部按运行时探测结果动态生成。
/// </summary>
public partial class EqualizerPage : ContentPage
{
    private readonly EqualizerService _equalizer;

    /// <summary>频段滑块的引用，便于复位后刷新数值显示。</summary>
    private readonly List<(int Band, Slider Slider, Label Value)> _bandControls = [];

    public EqualizerPage(EqualizerService equalizer)
    {
        InitializeComponent();

        _equalizer = equalizer;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // 若已有音频会话（正在播放/刚播过），立刻挂载一次
        _equalizer.AttachToCurrentSession();

        EnableSwitch.IsToggled = _equalizer.IsEnabled;
        Rebuild();
    }

    #region 构建界面

    private void Rebuild()
    {
        BuildStatus();
        BuildPresets();
        BuildBands();
    }

    private void BuildStatus()
    {
        if (!_equalizer.IsAvailable)
        {
            StatusLabel.Text = "当前设备或音频会话暂不支持均衡器（先播放一首歌再回来看看）";
            PresetCard.IsVisible = false;
            BandStack.IsVisible = false;
            return;
        }

        StatusLabel.Text = $"{_equalizer.BandCount} 频段 · 增益范围 " +
                           $"{_equalizer.MinLevel / 100.0:F1} ~ {_equalizer.MaxLevel / 100.0:F1} dB";
        PresetCard.IsVisible = true;
        BandStack.IsVisible = true;
    }

    private void BuildPresets()
    {
        PresetChips.Clear();
        if (!_equalizer.IsAvailable) return;

        for (var i = 0; i < _equalizer.PresetNames.Count; i++)
        {
            var index = i;
            var chip = new Border
            {
                BackgroundColor = ThemeService.Get("AmChipBg"),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(14, 7),
                Margin = new Thickness(0, 0, 8, 8),
                Content = new Label
                {
                    Text = _equalizer.PresetNames[index],
                    FontSize = 12,
                    TextColor = ThemeService.Get("AmTextSecondary"),
                },
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                _equalizer.UsePreset(index);
                SyncBandSliders();
            };
            chip.GestureRecognizers.Add(tap);

            PresetChips.Add(chip);
        }
    }

    private void BuildBands()
    {
        BandStack.Clear();
        _bandControls.Clear();

        if (!_equalizer.IsAvailable) return;

        for (var band = 0; band < _equalizer.BandCount; band++)
        {
            var index = band;
            var freq = band < _equalizer.CenterFrequencies.Count
                ? FormatFrequency(_equalizer.CenterFrequencies[band])
                : $"频段 {band + 1}";

            var valueLabel = new Label
            {
                FontSize = 12,
                TextColor = ThemeService.Get("AmTextTertiary"),
                WidthRequest = 58,
                HorizontalTextAlignment = TextAlignment.End,
                VerticalOptions = LayoutOptions.Center,
            };

            var slider = new Slider
            {
                Minimum = _equalizer.MinLevel,
                Maximum = _equalizer.MaxLevel,
                Value = _equalizer.GetSavedBandLevel(band),
                MinimumTrackColor = ThemeService.Get("AmPrimary"),
                MaximumTrackColor = ThemeService.Get("AmDivider"),
                ThumbColor = ThemeService.Get("AmPrimary"),
            };

            // 拖动过程中即时下发，听感更直接
            slider.ValueChanged += (_, e) =>
            {
                var level = (short)Math.Round(e.NewValue);
                valueLabel.Text = FormatLevel(level);
                _equalizer.SetBandLevel(index, level);
            };

            valueLabel.Text = FormatLevel((short)slider.Value);

            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                ],
                ColumnSpacing = 10,
            };

            var freqLabel = new Label
            {
                Text = freq,
                FontSize = 13,
                TextColor = ThemeService.Get("AmTextPrimary"),
                WidthRequest = 52,
                VerticalOptions = LayoutOptions.Center,
            };

            row.Add(freqLabel, 0);
            row.Add(slider, 1);
            row.Add(valueLabel, 2);

            BandStack.Add(row);
            _bandControls.Add((band, slider, valueLabel));
        }
    }

    private void SyncBandSliders()
    {
        foreach (var (band, slider, value) in _bandControls)
        {
            var level = _equalizer.GetSavedBandLevel(band);
            slider.Value = level;
            value.Text = FormatLevel(level);
        }
    }

    /// <summary>把中心频率换算成惯用写法：1000 → 1kHz。</summary>
    private static string FormatFrequency(int hz) =>
        hz >= 1000 ? $"{hz / 1000.0:0.#}kHz" : $"{hz}Hz";

    /// <summary>毫贝转 dB 文本。</summary>
    private static string FormatLevel(short millibel)
    {
        var db = millibel / 100.0;
        return db > 0 ? $"+{db:F1}" : $"{db:F1}";
    }

    #endregion

    #region 交互

    private void OnEnableToggled(object? sender, ToggledEventArgs e)
        => _equalizer.IsEnabled = e.Value;

    private void OnResetClicked(object? sender, EventArgs e)
    {
        _equalizer.ResetBands();
        SyncBandSliders();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        try { await Navigation.PopModalAsync(); }
        catch { /* 已经是栈底，忽略 */ }
    }

    #endregion
}
