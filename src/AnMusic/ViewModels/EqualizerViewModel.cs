using System.Collections.ObjectModel;
using AnMusic.Models;
using AnMusic.Services.Audio;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.ViewModels;

/// <summary>
/// 单个 EQ 频段的可观测包装。
/// </summary>
public partial class BandViewModel : ObservableObject
{
    public int Index { get; init; }
    public string Label { get; init; } = "";

    [ObservableProperty]
    private double _gain;

    internal Action<int, double>? GainChangedCallback;

    partial void OnGainChanged(double value) => GainChangedCallback?.Invoke(Index, value);
}

/// <summary>
/// 均衡器 ViewModel：10 频段滑块 + 预设管理 + 状态持久化。
/// </summary>
public partial class EqualizerViewModel : ObservableObject
{
    private readonly EqualizerService _eqService;
    private readonly UserSettingsService _settingsService;

    public BandViewModel[] Bands { get; }
    public ObservableCollection<EqualizerPreset> Presets { get; }

    [ObservableProperty]
    private EqualizerPreset? _selectedPreset;

    [ObservableProperty]
    private bool _isEnabled = true;

    private static readonly string[] FreqLabels = { "31", "62", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };

    public EqualizerViewModel(EqualizerService eqService, UserSettingsService settingsService)
    {
        _eqService = eqService;
        _settingsService = settingsService;

        // 恢复已保存的 EQ 状态（切歌保持 + 重启保持）
        var saved = settingsService.Settings;
        if (saved.EqualizerGains is { Length: 10 })
            _eqService.SetGains(saved.EqualizerGains);
        _eqService.Enabled = saved.EqualizerEnabled;

        Bands = new BandViewModel[10];
        for (int i = 0; i < 10; i++)
        {
            var band = new BandViewModel
            {
                Index = i,
                Label = FreqLabels[i],
                Gain = _eqService.GetBand(i)
            };
            band.GainChangedCallback = OnBandGainChanged;
            Bands[i] = band;
        }

        Presets =
        [
            EqualizerPreset.Flat,
            EqualizerPreset.Pop,
            EqualizerPreset.Rock,
            EqualizerPreset.Vocal,
            EqualizerPreset.BassBoost
        ];

        _isEnabled = saved.EqualizerEnabled;
    }

    private void OnBandGainChanged(int index, double gain)
    {
        _eqService.SetBand(index, (float)gain);
        SelectedPreset = null; // 手动调整后取消预设选中
        PersistState();
    }

    /// <summary>将当前 EQ 状态写入用户设置。</summary>
    private void PersistState()
    {
        _settingsService.Update(s =>
        {
            s.EqualizerGains = _eqService.GetCurrentGains();
            s.EqualizerEnabled = _eqService.Enabled;
        });
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        if (SelectedPreset is null) return;
        _eqService.ApplyPreset(SelectedPreset);
        for (int i = 0; i < 10; i++)
            Bands[i].Gain = _eqService.GetBand(i);
        PersistState();
    }

    [RelayCommand]
    private void Reset()
    {
        _eqService.Reset();
        for (int i = 0; i < 10; i++)
            Bands[i].Gain = 0;
        SelectedPreset = EqualizerPreset.Flat;
        PersistState();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _eqService.Enabled = value;
        PersistState();
    }
}
