using AnMusic.Models;
using NAudio.Effects;
using NAudio.Wave;

namespace AnMusic.Services.Audio;

/// <summary>
/// 均衡器服务：管理 10 频段 EQ 增益（跨歌保持），为每首新歌创建 GraphicEqualizer 实例。
/// 增益数组复用，GraphicEqualizer 实例随 track 重建（绑定 sample rate，由 EffectSampleProvider 配置）。
/// 使用 NAudio 3 的新 NAudio.Effects 框架（替代已移除的 NAudio.Extras.Equalizer）。
/// </summary>
public sealed class EqualizerService
{
    private readonly float[] _gains = new float[10]; // -12 ~ +12 dB，0 = 平直
    private GraphicEqualizer? _equalizer;
    private bool _enabled = true;

    /// <summary>10 频段中心频率 (Hz)，与 GraphicEqualizerLayout.TenBandOctave 一致。</summary>
    public static readonly float[] Frequencies = [31.5f, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            // AudioEffect.Bypass 是 click-free 的，可在线切换
            if (_equalizer is not null)
                _equalizer.Bypass = !value;
        }
    }

    public EqualizerService()
    {
        // 默认全部 0 dB（平直）
        Array.Fill(_gains, 0f);
    }

    /// <summary>为给定 source 创建 EQ 链，返回带 EQ 的 ISampleProvider。</summary>
    /// <remarks>EffectSampleProvider 会在构造时用 source.WaveFormat 配置 effect。</remarks>
    public ISampleProvider CreateChain(ISampleProvider source)
    {
        _equalizer = new GraphicEqualizer(GraphicEqualizerLayout.TenBandOctave);
        // 应用当前增益到新实例
        for (int i = 0; i < 10; i++)
            _equalizer.SetBandGain(i, _gains[i]);
        _equalizer.Bypass = !_enabled;
        return new EffectSampleProvider(source, _equalizer);
    }

    /// <summary>设置指定频段增益（dB）。</summary>
    public void SetBand(int index, float gainDb)
    {
        if (index < 0 || index >= 10) return;
        _gains[index] = Math.Clamp(gainDb, -12f, 12f);
        _equalizer?.SetBandGain(index, _gains[index]); // 内部 click-free
    }

    /// <summary>获取指定频段当前增益。</summary>
    public float GetBand(int index) =>
        (index >= 0 && index < 10) ? _gains[index] : 0f;

    /// <summary>应用预设。</summary>
    public void ApplyPreset(EqualizerPreset preset)
    {
        if (preset.Gains.Length != 10) return;
        for (int i = 0; i < 10; i++)
        {
            _gains[i] = Math.Clamp(preset.Gains[i], -12f, 12f);
            _equalizer?.SetBandGain(i, _gains[i]);
        }
    }

    /// <summary>重置所有频段为 0 dB。</summary>
    public void Reset()
    {
        for (int i = 0; i < 10; i++)
        {
            _gains[i] = 0f;
            _equalizer?.SetBandGain(i, 0f);
        }
    }

    /// <summary>将当前 bands 状态重新应用到 active equalizer（切歌后调用）。</summary>
    public void ApplyCurrentBands()
    {
        if (_equalizer is null) return;
        for (int i = 0; i < 10; i++)
            _equalizer.SetBandGain(i, _gains[i]);
    }

    /// <summary>获取当前所有频段增益数组副本。</summary>
    public float[] GetCurrentGains()
    {
        var gains = new float[10];
        Array.Copy(_gains, gains, 10);
        return gains;
    }

    /// <summary>直接设置所有频段增益。</summary>
    public void SetGains(float[] gains)
    {
        if (gains.Length != 10) return;
        for (int i = 0; i < 10; i++)
        {
            _gains[i] = Math.Clamp(gains[i], -12f, 12f);
            _equalizer?.SetBandGain(i, _gains[i]);
        }
    }
}
