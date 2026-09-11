using Android.Media.Audiofx;
using AnMusic.Services;
using AnMusic.Services.Settings;

namespace AnMusic.Android.Services;

/// <summary>
/// 均衡器服务：把系统 <see cref="Equalizer"/> 挂到当前 MediaPlayer 的音频会话上。
/// </summary>
/// <remarks>
/// 几个必须知道的点：
/// 1. <c>Equalizer</c> 必须绑定一个「活的」音频会话，而 MediaPlayer 每次
///    <c>Reset()</c> 都会换会话 Id —— 所以换歌之后要重新挂载，不能只建一次。
/// 2. 频段数量、增益范围、可用预设都由设备决定，不能写死。
/// 3. 增益单位是毫贝（millibel），100 = 1 dB。
/// 4. 所有调用都可能因设备未实现音效而抛异常，一律吞掉并标记为不可用，
///    不能让均衡器把播放流程带崩。
/// </remarks>
public sealed class EqualizerService : IDisposable
{
    private readonly AndroidAudioEngine _engine;
    private readonly UserSettingsService _settings;

    private Equalizer? _equalizer;
    private int _attachedSession = -1;
    private bool _disposed;

    public EqualizerService(AndroidAudioEngine engine, UserSettingsService settings)
    {
        _engine = engine;
        _settings = settings;

        // 会话就绪后自动（重新）挂载
        _engine.AudioSessionReady += (_, _) => AttachToCurrentSession();
    }

    #region 设备能力

    /// <summary>设备是否支持均衡器。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>频段数量（多数设备为 5）。</summary>
    public int BandCount { get; private set; }

    /// <summary>增益下限（毫贝，通常 -1500 即 -15 dB）。</summary>
    public short MinLevel { get; private set; } = -1500;

    /// <summary>增益上限（毫贝）。</summary>
    public short MaxLevel { get; private set; } = 1500;

    /// <summary>各频段中心频率（Hz）。</summary>
    public IReadOnlyList<int> CenterFrequencies { get; private set; } = [];

    /// <summary>设备内置预设名。</summary>
    public IReadOnlyList<string> PresetNames { get; private set; } = [];

    #endregion

    #region 状态

    /// <summary>均衡器开关（持久化在 settings.json，与桌面端共用字段）。</summary>
    public bool IsEnabled
    {
        get => _settings.Settings.EqualizerEnabled;
        set
        {
            _settings.Update(s => s.EqualizerEnabled = value);
            ApplyEnabled();
        }
    }

    /// <summary>读取某频段的当前增益（毫贝）。</summary>
    public short GetBandLevel(int band)
    {
        try
        {
            return _equalizer is null ? (short)0 : _equalizer.GetBandLevel((short)band);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>从持久化数据里取某频段的增益；没有记录时返回 0。</summary>
    public short GetSavedBandLevel(int band)
    {
        var gains = _settings.Settings.EqualizerGains;
        if (gains is null || band < 0 || band >= gains.Length) return 0;
        return (short)Math.Clamp(gains[band], MinLevel, MaxLevel);
    }

    /// <summary>设置某频段增益（毫贝）并落盘。</summary>
    public void SetBandLevel(int band, short level)
    {
        level = (short)Math.Clamp(level, MinLevel, MaxLevel);

        try
        {
            _equalizer?.SetBandLevel((short)band, level);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("设置均衡器频段", ex, $"band={band}");
        }

        SaveGains(band, level);
    }

    /// <summary>把所有频段复位到 0 dB。</summary>
    public void ResetBands()
    {
        for (var i = 0; i < BandCount; i++)
            SetBandLevel(i, 0);
    }

    /// <summary>套用设备内置预设。</summary>
    public void UsePreset(int presetIndex)
    {
        try
        {
            _equalizer?.UsePreset((short)presetIndex);

            // 预设生效后把各频段实际增益读回来存下，切回应用时能保持一致
            var gains = new float[BandCount];
            for (var i = 0; i < BandCount; i++)
                gains[i] = GetBandLevel(i);

            _settings.Update(s => s.EqualizerGains = gains);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("套用均衡器预设", ex, $"preset={presetIndex}");
        }
    }

    #endregion

    #region 挂载

    /// <summary>
    /// 挂到当前音频会话。换歌导致会话变化时会被再次调用，
    /// 此时先释放旧实例再重建（同一个会话重复挂载会拿到旧对象，必须换新）。
    /// </summary>
    public void AttachToCurrentSession()
    {
        if (_disposed) return;

        var session = _engine.AudioSessionId;
        if (session <= 0 || session == _attachedSession) return;

        Release();

        try
        {
            _equalizer = new Equalizer(0, session);
            _attachedSession = session;

            BandCount = _equalizer.NumberOfBands;

            var range = _equalizer.GetBandLevelRange();
            if (range is { Length: >= 2 })
            {
                MinLevel = range[0];
                MaxLevel = range[1];
            }

            var freqs = new List<int>();
            for (short b = 0; b < BandCount; b++)
                freqs.Add(_equalizer.GetCenterFreq(b));
            CenterFrequencies = freqs;

            var presets = new List<string>();
            for (short p = 0; p < _equalizer.NumberOfPresets; p++)
            {
                var name = _equalizer.GetPresetName(p);
                if (!string.IsNullOrWhiteSpace(name)) presets.Add(name!);
            }
            PresetNames = presets;

            IsAvailable = BandCount > 0;

            // 恢复用户的增益设置与开关状态
            for (short b = 0; b < BandCount; b++)
                _equalizer.SetBandLevel(b, GetSavedBandLevel(b));

            ApplyEnabled();
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            BandCount = 0;
            AppPaths.LogError("挂载均衡器", ex, $"session={session}");
            Release();
        }
    }

    private void ApplyEnabled()
    {
        try
        {
            _equalizer?.SetEnabled(IsEnabled);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("开关均衡器", ex);
        }
    }

    private void SaveGains(int band, short level)
    {
        var gains = _settings.Settings.EqualizerGains;
        var length = Math.Max(BandCount, band + 1);

        if (gains is null || gains.Length < length)
        {
            var grown = new float[length];
            if (gains is not null) Array.Copy(gains, grown, Math.Min(gains.Length, length));
            gains = grown;
        }

        gains[band] = level;
        _settings.Update(s => s.EqualizerGains = gains);
    }

    private void Release()
    {
        if (_equalizer is null) return;

        try
        {
            _equalizer.SetEnabled(false);
            _equalizer.Release();
        }
        catch
        {
            // 释放失败无所谓，对象会被 GC 掉
        }

        _equalizer = null;
        _attachedSession = -1;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release();
        _equalizer?.Dispose();
        _equalizer = null;
    }
}
