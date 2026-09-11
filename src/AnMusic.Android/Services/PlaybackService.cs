using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using AnMusic.Android.ViewModels;
using AnMusic.Services;
using Microsoft.Extensions.DependencyInjection;
using AndroidAudioFocus = global::Android.Media.AudioFocus;
using AndroidAudioFocusRequest = global::Android.Media.AudioFocusRequest;
using AndroidAudioManager = global::Android.Media.AudioManager;
using AndroidBitmap = global::Android.Graphics.Bitmap;
using AndroidBitmapFactory = global::Android.Graphics.BitmapFactory;
using AndroidMetadata = global::Android.Media.MediaMetadata;
using AndroidPlaybackState = global::Android.Media.Session.PlaybackState;
using AndroidPlaybackStateCode = global::Android.Media.Session.PlaybackStateCode;
using AndroidStream = global::Android.Media.Stream;
using MediaSession = global::Android.Media.Session.MediaSession;
using PlaybackState = AnMusic.Models.PlaybackState;

namespace AnMusic.Android.Services;

/// <summary>
/// 后台播放前台服务：把播放行为从 UI 生命周期里剥离出来，并提供通知栏 / 锁屏 / 蓝牙耳机控制。
/// </summary>
/// <remarks>
/// 对应桌面端的 <c>MediaSessionService</c>（SMTC）。此前安卓端完全没有这一层，
/// 后果是：切后台或锁屏后系统随时可能回收进程导致音乐中断，通知栏也没有任何控制入口。
///
/// 这里用平台自带的 <see cref="MediaSession"/> + <c>Notification.MediaStyle</c>，
/// 不引入 AndroidX Media 依赖：MediaSession 自 API 21 起可用，而本项目 minSdk 24，
/// 因此无需 compat 版本；系统会把会话自动接入锁屏、蓝牙耳机按键与车机。
///
/// 服务与 <see cref="PlayerViewModel"/> 同进程，直接取单例并订阅其状态变化来刷新通知，
/// 不额外造一套跨进程通信。
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    private const string ChannelId = "anmusic_playback";
    private const int NotificationId = 1001;

    public const string ActionPlayPause = "com.painterankry.anmusic.action.PLAY_PAUSE";
    public const string ActionNext = "com.painterankry.anmusic.action.NEXT";
    public const string ActionPrevious = "com.painterankry.anmusic.action.PREVIOUS";
    public const string ActionStop = "com.painterankry.anmusic.action.STOP";

    private MediaSession? _session;
    private AndroidAudioManager? _audioManager;
    private AudioManagerFocusListener? _focusListener;
    private BecomingNoisyReceiver? _noisyReceiver;
    private PlayerViewModel? _player;
    private readonly Handler _mainHandler = new(Looper.MainLooper!);

    /// <summary>周期性刷新媒体会话位置，让锁屏进度条不漂移。</summary>
    private readonly Handler _tickHandler = new(Looper.MainLooper!);

    private bool _foreground;
    private bool _hasAudioFocus;
    private string? _cachedArtPath;
    private AndroidBitmap? _cachedArt;

    /// <summary>服务实例：供播放器在播放开始时请求启动。</summary>
    private static PlaybackService? _instance;

    #region 生命周期

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;

        try
        {
            CreateNotificationChannel();

            _audioManager = GetSystemService(AudioService) as AndroidAudioManager;
            _focusListener = new AudioManagerFocusListener(OnAudioFocusChanged);

            _session = new MediaSession(this, "AnMusic");
            _session.SetFlags(global::Android.Media.Session.MediaSessionFlags.HandlesMediaButtons
                            | global::Android.Media.Session.MediaSessionFlags.HandlesTransportControls);
            _session.SetCallback(new SessionCallback(this));
            _session.Active = true;

            _noisyReceiver = new BecomingNoisyReceiver(this);
            RegisterReceiver(_noisyReceiver, new IntentFilter(AndroidAudioManager.ActionAudioBecomingNoisy));

            AttachPlayer();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("初始化播放服务", ex);
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        try
        {
            switch (action)
            {
                case ActionPlayPause:
                    _player?.PlayPauseCommand.Execute(null);
                    break;
                case ActionNext:
                    _player?.NextCommand.Execute(null);
                    break;
                case ActionPrevious:
                    _player?.PreviousCommand.Execute(null);
                    break;
                case ActionStop:
                    _player?.Pause();
                    StopPlaybackAndSelf();
                    return StartCommandResult.NotSticky;
            }

            // 必须在 5 秒内进入前台，否则系统抛 ANR/RemoteServiceException
            if (!_foreground) PromoteToForeground();
            else RefreshNotification();
        }
        catch (Exception ex)
        {
            AppPaths.LogError("处理播放服务指令", ex, action);
        }

        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _instance = null;

        try { _tickHandler.RemoveCallbacksAndMessages(null); } catch { }
        try { if (_noisyReceiver is not null) UnregisterReceiver(_noisyReceiver); } catch { }

        if (_player is not null) _player.PropertyChanged -= OnPlayerPropertyChanged;

        try { _session?.SetCallback(null); } catch { }
        try { if (_session is not null) _session.Active = false; } catch { }
        try { _session?.Release(); } catch { }
        _session = null;

        AbandonAudioFocus();
        _cachedArt = null;
        _cachedArtPath = null;

        base.OnDestroy();
    }

    /// <summary>用户从最近任务里划掉应用：正在播放就继续（前台服务本就该活下来），否则收工。</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        if (_player?.IsPlaying == true) return;
        StopPlaybackAndSelf();
        base.OnTaskRemoved(rootIntent);
    }

    #endregion

    #region 对外入口

    /// <summary>
    /// 播放开始时调用：确保服务已启动并处于前台。
    /// 重复调用是安全的（已有实例时只刷新通知）。
    /// </summary>
    public static void EnsureStarted()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var intent = new Intent(context, typeof(PlaybackService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("启动播放服务", ex);
        }
    }

    /// <summary>播放结束（暂停且无曲目、或用户主动停止）时收起通知。</summary>
    public static void StopService()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(PlaybackService)));
        }
        catch (Exception ex)
        {
            AppPaths.LogError("停止播放服务", ex);
        }
    }

    #endregion

    #region 播放器联动

    private void AttachPlayer()
    {
        try
        {
            _player = MauiProgram.Services.GetRequiredService<PlayerViewModel>();
            _player.PropertyChanged += OnPlayerPropertyChanged;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("播放服务绑定播放器", ex);
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.CurrentTrack):
                _cachedArt = null;
                _cachedArtPath = null;
                UpdateSessionMetadata();
                UpdateSessionState();
                RefreshNotification();
                break;
            case nameof(PlayerViewModel.IsPlaying):
                if (_player?.IsPlaying == true) RequestAudioFocus();
                UpdateSessionState();
                RefreshNotification();
                ScheduleTick();
                break;
            case nameof(PlayerViewModel.IsBuffering):
                UpdateSessionState();
                break;
        }
    }

    #endregion

    #region 媒体会话

    private void UpdateSessionMetadata()
    {
        if (_session is null) return;

        var track = _player?.CurrentTrack;
        var builder = new AndroidMetadata.Builder()
            .PutString(AndroidMetadata.MetadataKeyTitle, track?.Title ?? "未在播放")
            .PutString(AndroidMetadata.MetadataKeyArtist, track?.Artist ?? string.Empty);

        if (track is not null)
        {
            if (!string.IsNullOrEmpty(track.Album))
                builder.PutString(AndroidMetadata.MetadataKeyAlbum, track.Album);
            if (track.Duration > TimeSpan.Zero)
                builder.PutLong(AndroidMetadata.MetadataKeyDuration, (long)track.Duration.TotalMilliseconds);
            builder.PutString(AndroidMetadata.MetadataKeyMediaId, $"{track.ProviderId}:{track.Id}");

            var art = LoadArt(track.CoverKey);
            if (art is not null) builder.PutBitmap(AndroidMetadata.MetadataKeyAlbumArt, art);
        }

        _session.SetMetadata(builder.Build());
    }

    private void UpdateSessionState()
    {
        if (_session is null) return;

        var playing = _player?.IsPlaying == true;
        var buffering = _player?.IsBuffering == true;
        var state = buffering
            ? AndroidPlaybackStateCode.Buffering
            : playing ? AndroidPlaybackStateCode.Playing : AndroidPlaybackStateCode.Paused;

        var positionMs = (long)((_player?.PositionSeconds ?? 0) * 1000);
        var actions = AndroidPlaybackState.ActionPlay
                    | AndroidPlaybackState.ActionPause
                    | AndroidPlaybackState.ActionPlayPause
                    | AndroidPlaybackState.ActionSkipToNext
                    | AndroidPlaybackState.ActionSkipToPrevious
                    | AndroidPlaybackState.ActionSeekTo
                    | AndroidPlaybackState.ActionStop;

        _session.SetPlaybackState(new AndroidPlaybackState.Builder()
            .SetActions(actions)
            .SetState(state, positionMs, playing ? 1.0f : 0.0f)
            .Build());
    }

    /// <summary>每 3 秒同步一次位置，让锁屏进度条与内嵌进度显示准确。</summary>
    private void ScheduleTick()
    {
        _tickHandler.RemoveCallbacksAndMessages(null);
        if (_player?.IsPlaying != true) return;

        _tickHandler.PostDelayed(() =>
        {
            UpdateSessionState();
            ScheduleTick();
        }, 3000);
    }

    private AndroidBitmap? LoadArt(string? coverKey)
    {
        if (string.IsNullOrEmpty(coverKey) || !File.Exists(coverKey))
            return null;
        if (string.Equals(_cachedArtPath, coverKey, StringComparison.Ordinal))
            return _cachedArt;

        try
        {
            // 通知栏只会缩到几百像素，先按边界采样再解码，避免把 4000×4000 的大图读进内存
            var bounds = new AndroidBitmapFactory.Options { InJustDecodeBounds = true };
            AndroidBitmapFactory.DecodeFile(coverKey, bounds);

            var sample = 1;
            var longest = Math.Max(bounds.OutWidth, bounds.OutHeight);
            while (longest / sample > 512) sample *= 2;

            var options = new AndroidBitmapFactory.Options { InSampleSize = sample };
            var art = AndroidBitmapFactory.DecodeFile(coverKey, options);

            _cachedArtPath = coverKey;
            _cachedArt = art;
            return art;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("加载通知栏封面", ex, coverKey);
            return null;
        }
    }

    #endregion

    #region 音频焦点

    private void RequestAudioFocus()
    {
        if (_hasAudioFocus || _audioManager is null || _focusListener is null) return;
        try
        {
            var result = _audioManager.RequestAudioFocus(_focusListener, AndroidStream.Music, AndroidAudioFocus.Gain);
            _hasAudioFocus = result == AndroidAudioFocusRequest.Granted;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("申请音频焦点", ex);
        }
    }

    private void AbandonAudioFocus()
    {
        if (!_hasAudioFocus || _audioManager is null || _focusListener is null) return;
        try { _audioManager.AbandonAudioFocus(_focusListener); } catch { }
        _hasAudioFocus = false;
    }

    private void OnAudioFocusChanged(AndroidAudioFocus change)
    {
        _mainHandler.Post(() =>
        {
            try
            {
                switch (change)
                {
                    case AndroidAudioFocus.Loss:
                    case AndroidAudioFocus.LossTransient:
                        // 被电话/其它播放器抢走：暂停，等系统还给焦点由用户手动续播
                        _player?.Pause();
                        _hasAudioFocus = false;
                        break;
                    case AndroidAudioFocus.LossTransientCanDuck:
                        // 导航播报之类：压低音量而不是打断
                        _player?.Duck();
                        break;
                    case AndroidAudioFocus.Gain:
                        _player?.Unduck();
                        _hasAudioFocus = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                AppPaths.LogError("处理音频焦点变化", ex);
            }
        });
    }

    /// <summary>耳机拔出：立刻暂停（系统行为预期，避免外放尴尬）。</summary>
    private void OnBecomingNoisy() => _mainHandler.Post(() => _player?.Pause());

    #endregion

    #region 通知

    private void CreateNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager is null) return;

        var channel = new NotificationChannel(ChannelId, "播放控制", NotificationImportance.Low)
        {
            Description = "显示当前播放的歌曲，并提供播放控制按钮"
        };
        channel.SetShowBadge(false);
        channel.LockscreenVisibility = NotificationVisibility.Public;
        manager.CreateNotificationChannel(channel);
    }

    private void PromoteToForeground()
    {
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);
        _foreground = true;
    }

    private void RefreshNotification()
    {
        if (!_foreground) return;
        try
        {
            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.Notify(NotificationId, BuildNotification());
        }
        catch (Exception ex)
        {
            AppPaths.LogError("刷新播放通知", ex);
        }
    }

    private Notification BuildNotification()
    {
        var track = _player?.CurrentTrack;
        var playing = _player?.IsPlaying == true;

        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        builder
            .SetContentTitle(track?.Title ?? "AnMusic")
            .SetContentText(track?.Artist ?? "未在播放")
            .SetSmallIcon(ResolveSmallIcon())
            .SetContentIntent(BuildOpenAppIntent())
            .SetDeleteIntent(BuildServiceAction(ActionStop, 4))
            .SetOngoing(playing)
            .SetShowWhen(false)
            .SetOnlyAlertOnce(true)
            .SetVisibility(NotificationVisibility.Public);

        var art = LoadArt(track?.CoverKey);
        if (art is not null) builder.SetLargeIcon(art);

        builder.AddAction(new Notification.Action.Builder(
            global::Android.Resource.Drawable.IcMediaPrevious, "上一首", BuildServiceAction(ActionPrevious, 1)).Build());
        builder.AddAction(new Notification.Action.Builder(
            playing ? global::Android.Resource.Drawable.IcMediaPause : global::Android.Resource.Drawable.IcMediaPlay,
            playing ? "暂停" : "播放",
            BuildServiceAction(ActionPlayPause, 2)).Build());
        builder.AddAction(new Notification.Action.Builder(
            global::Android.Resource.Drawable.IcMediaNext, "下一首", BuildServiceAction(ActionNext, 3)).Build());

        var style = new Notification.MediaStyle()
            .SetMediaSession(_session!.SessionToken!)
            .SetShowActionsInCompactView(0, 1, 2);
        builder.SetStyle(style);

        return builder.Build()!;
    }

    private int ResolveSmallIcon()
    {
        // 优先用应用图标；拿不到就退回系统媒体图标，保证通知永远有图标可显示
        var icon = ApplicationInfo?.Icon ?? 0;
        return icon != 0 ? icon : global::Android.Resource.Drawable.IcMediaPlay;
    }

    private PendingIntent BuildOpenAppIntent()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private PendingIntent BuildServiceAction(string action, int requestCode)
    {
        var intent = new Intent(this, typeof(PlaybackService)).SetAction(action);
        return PendingIntent.GetService(this, requestCode, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private void StopPlaybackAndSelf()
    {
        try
        {
            AbandonAudioFocus();
            if (_session is not null) _session.Active = false;
            StopForeground(StopForegroundFlags.Remove);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("停止前台服务", ex);
        }
        finally
        {
            _foreground = false;
            StopSelf();
        }
    }

    #endregion

    #region 内部类型

    /// <summary>媒体会话回调：承接系统/蓝牙耳机/锁屏发来的控制指令。</summary>
    private sealed class SessionCallback : MediaSession.Callback
    {
        private readonly PlaybackService _owner;
        public SessionCallback(PlaybackService owner) => _owner = owner;

        public override void OnPlay() => _owner._mainHandler.Post(() => _owner._player?.Play());
        public override void OnPause() => _owner._mainHandler.Post(() => _owner._player?.Pause());
        public override void OnStop() => _owner._mainHandler.Post(() => _owner._player?.Pause());

        public override void OnSkipToNext() =>
            _owner._mainHandler.Post(() => _owner._player?.NextCommand.Execute(null));

        public override void OnSkipToPrevious() =>
            _owner._mainHandler.Post(() => _owner._player?.PreviousCommand.Execute(null));

        public override void OnSeekTo(long pos) =>
            _owner._mainHandler.Post(() =>
            {
                if (_owner._player is { } p) p.CommitSeek(pos / 1000.0);
            });
    }

    /// <summary>音频焦点监听（接口要求实现 Java 对象）。</summary>
    private sealed class AudioManagerFocusListener : Java.Lang.Object, AndroidAudioManager.IOnAudioFocusChangeListener
    {
        private readonly Action<AndroidAudioFocus> _onChange;
        public AudioManagerFocusListener(Action<AndroidAudioFocus> onChange) => _onChange = onChange;
        public void OnAudioFocusChange(AndroidAudioFocus focusChange) => _onChange(focusChange);
    }

    /// <summary>耳机拔出广播。</summary>
    private sealed class BecomingNoisyReceiver : BroadcastReceiver
    {
        private readonly PlaybackService _owner;
        public BecomingNoisyReceiver(PlaybackService owner) => _owner = owner;
        public override void OnReceive(Context? context, Intent? intent) => _owner.OnBecomingNoisy();
    }

    #endregion
}
