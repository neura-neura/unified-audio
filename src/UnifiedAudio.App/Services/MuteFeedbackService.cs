using UnifiedAudio.Models;
using UnifiedAudio.Views;
using Microsoft.UI.Dispatching;
using UnifiedAudio.Interop;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace UnifiedAudio.Services;

public sealed class MuteFeedbackService : IDisposable
{
    private readonly EngineClient _engine;
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly DispatcherQueueTimer _overlayTimer;
    private MuteOsdWindow? _osd;
    private MuteOverlayWindow? _overlay;
    private bool _polling;
    private bool _disposed;
    private DateTimeOffset _manualOverlayRevealUntil;
    private readonly object _playersLock = new();
    private readonly Dictionary<MediaPlayer, IRandomAccessStream?> _activePlayers = [];
    public event EventHandler? SettingsChanged;

    public MuteFeedbackService(DispatcherQueue dispatcher, EngineClient engine, AppSettings settings, AppLog log)
    {
        _engine = engine;
        _settings = settings;
        _log = log;
        _overlayTimer = dispatcher.CreateTimer();
        _overlayTimer.Interval = TimeSpan.FromMilliseconds(250);
        _overlayTimer.Tick += OverlayTimer_Tick;
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (_disposed) return;
        if (_settings.ShowMuteOverlay)
        {
            if (_overlay is null)
            {
                _overlay = new MuteOverlayWindow();
                _overlay.PlacementChanged = (monitorId, x, y) =>
                {
                    _settings.OverlayRelativeX = x;
                    _settings.OverlayRelativeY = y;
                    _settings.OverlayMonitorId = monitorId;
                    _settings.OverlayMonitorPlacements[monitorId] = new OverlayMonitorPlacement
                    {
                        RelativeX = x,
                        RelativeY = y
                    };
                    SettingsChanged?.Invoke(this, EventArgs.Empty);
                };
            }
            _overlay.ApplySettings(_settings);
            _overlay.SetVisible(true);
            _overlayTimer.Start();
            _ = RefreshOverlayAsync();
        }
        else
        {
            _overlayTimer.Stop();
            _overlay?.SetVisible(false);
        }
    }

    public void ToggleOverlayVisibility()
    {
        _settings.ShowMuteOverlay = !_settings.ShowMuteOverlay;
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Enables the overlay and puts it on a visible monitor immediately. This
    /// is intentionally a user-facing recovery action: it also repairs a
    /// window that was persisted outside the current display topology.
    /// </summary>
    public void ShowOverlayNow()
    {
        if (_disposed) return;
        _settings.ShowMuteOverlay = true;
        _manualOverlayRevealUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleOverlayLock()
    {
        _settings.OverlayLocked = !_settings.OverlayLocked;
        ApplySettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifyMuteChanged(EngineSnapshot snapshot, string origin)
    {
        if (_settings.PlayMuteFeedbackSounds)
        {
            _ = PlayFeedbackSoundAsync(snapshot.MutedRequested, origin);
        }
        if (_settings.ShowMuteOsd && !(_settings.ExcludeFullscreenOsd && IsForegroundFullscreen()))
        {
            _osd ??= new MuteOsdWindow();
            _osd.ShowState(snapshot.MutedRequested, _settings);
        }
        UpdateOverlay(snapshot);
        _log.Info($"Mute feedback emitted. State={snapshot.MutedRequested}; settled={snapshot.MuteSettled}; origin={origin}.");
    }

    private async void OverlayTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        await RefreshOverlayAsync();
    }

    private async Task RefreshOverlayAsync()
    {
        if (_polling || !_settings.ShowMuteOverlay) return;
        if (!_engine.IsConnected)
        {
            _overlay?.UpdateState(muted: false, speaking: false, engineAvailable: false);
            return;
        }
        _polling = true;
        try
        {
            UpdateOverlay(await _engine.GetSnapshotAsync());
        }
        catch (Exception ex)
        {
            _log.Warn($"Overlay refresh failed: {ex.Message}");
            _overlay?.UpdateState(muted: false, speaking: false, engineAvailable: false);
        }
        finally
        {
            _polling = false;
        }
    }

    private void UpdateOverlay(EngineSnapshot snapshot)
    {
        if (_overlay is null) return;
        var peakDb = snapshot.VoicePeak <= 0 ? -160.0 : 20.0 * Math.Log10(snapshot.VoicePeak);
        _overlay.UpdateState(snapshot.MutedRequested, !snapshot.MutedRequested && peakDb >= _settings.OverlayActivityThresholdDb);
        var visibleForState = _settings.OverlayVisibility switch
        {
            OverlayVisibilityMode.MutedOnly => snapshot.MutedRequested,
            OverlayVisibilityMode.UnmutedOnly => !snapshot.MutedRequested,
            _ => true
        };
        var manuallyRevealed = _manualOverlayRevealUntil > DateTimeOffset.UtcNow;
        _overlay.SetVisible(_settings.ShowMuteOverlay && (visibleForState || manuallyRevealed));
    }

    private static bool IsForegroundFullscreen()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == nint.Zero || !NativeMethods.GetWindowRect(window, out var rect)) return false;
        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return false;
        const int tolerance = 2;
        return rect.Left <= info.Monitor.Left + tolerance
            && rect.Top <= info.Monitor.Top + tolerance
            && rect.Right >= info.Monitor.Right - tolerance
            && rect.Bottom >= info.Monitor.Bottom - tolerance;
    }

    private async Task PlayFeedbackSoundAsync(bool muted, string origin)
    {
        var ptt = origin.Contains("PTT", StringComparison.OrdinalIgnoreCase)
            || origin.Contains("Push-to-mute", StringComparison.OrdinalIgnoreCase);
        var type = (ptt, muted) switch
        {
            (true, true) => 0x00000020u,
            (true, false) => 0x00000000u,
            (false, true) => 0x00000010u,
            _ => 0x00000040u
        };
        var customPath = (ptt, muted) switch
        {
            (true, true) => _settings.PttOffSoundPath,
            (true, false) => _settings.PttOnSoundPath,
            (false, true) => _settings.MuteSoundPath,
            _ => _settings.UnmuteSoundPath
        };
        var volumePercent = (ptt, muted) switch
        {
            (true, true) => _settings.PttOffSoundVolumePercent,
            (true, false) => _settings.PttOnSoundVolumePercent,
            (false, true) => _settings.MuteSoundVolumePercent,
            _ => _settings.UnmuteSoundVolumePercent
        };
        try
        {
            var player = new MediaPlayer();
            player.CommandManager.IsEnabled = false;
            // MediaPlayer.Volume is the actual per-player gain. Keep the
            // setting in percent for the UI, but never pass an out-of-range
            // value to WinRT (including legacy/corrupt settings files).
            player.Volume = Math.Clamp(volumePercent, 0, 100) / 100.0;
            if (!string.IsNullOrWhiteSpace(_settings.FeedbackOutputDeviceId))
            {
                player.AudioDevice = await DeviceInformation.CreateFromIdAsync(_settings.FeedbackOutputDeviceId);
            }

            IRandomAccessStream? stream = null;
            if (!string.IsNullOrWhiteSpace(customPath)
                && string.Equals(Path.GetExtension(customPath), ".wav", StringComparison.OrdinalIgnoreCase)
                && File.Exists(customPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(customPath);
                player.Source = MediaSource.CreateFromStorageFile(file);
            }
            else
            {
                stream = await CreateToneAsync((ptt, muted) switch
                {
                    (true, true) => (520.0, 90),
                    (true, false) => (980.0, 90),
                    (false, true) => (360.0, 130),
                    _ => (760.0, 130)
                });
                player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            }

            player.MediaEnded += PlayerFinished;
            player.MediaFailed += PlayerFailed;
            lock (_playersLock) _activePlayers[player] = stream;
            player.Play();
        }
        catch (Exception ex)
        {
            _log.Warn($"Feedback audio failed on '{_settings.FeedbackOutputDeviceName}': {ex.Message}");
            // A zero-volume setting must remain silent even when the preferred
            // MediaPlayer path is unavailable and the legacy fallback is used.
            if (volumePercent > 0)
                Interop.NativeMethods.MessageBeep(type);
        }
    }

    private static async Task<IRandomAccessStream> CreateToneAsync((double Frequency, int DurationMs) tone)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bits = 16;
        var sampleCount = sampleRate * tone.DurationMs / 1000;
        var pcmBytes = sampleCount * sizeof(short);
        using var bytes = new MemoryStream(44 + pcmBytes);
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcmBytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8);
            writer.Write((short)(channels * bits / 8));
            writer.Write(bits);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmBytes);
            for (var index = 0; index < sampleCount; index++)
            {
                var edge = Math.Min(index, sampleCount - 1 - index);
                var envelope = Math.Clamp(edge / (sampleRate * 0.008), 0.0, 1.0);
                var sample = Math.Sin(2.0 * Math.PI * tone.Frequency * index / sampleRate) * envelope * 0.24;
                writer.Write((short)(sample * short.MaxValue));
            }
        }

        var stream = new InMemoryRandomAccessStream();
        using var output = stream.GetOutputStreamAt(0);
        using var dataWriter = new DataWriter(output);
        dataWriter.WriteBytes(bytes.ToArray());
        await dataWriter.StoreAsync();
        await dataWriter.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    private void PlayerFinished(MediaPlayer sender, object args) => ReleasePlayer(sender);

    private void PlayerFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _log.Warn($"Feedback playback failed: {args.ErrorMessage}");
        ReleasePlayer(sender);
    }

    private void ReleasePlayer(MediaPlayer player)
    {
        IRandomAccessStream? stream = null;
        lock (_playersLock)
        {
            if (_activePlayers.Remove(player, out var retainedStream)) stream = retainedStream;
        }
        player.MediaEnded -= PlayerFinished;
        player.MediaFailed -= PlayerFailed;
        player.Dispose();
        stream?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _overlayTimer.Stop();
        _overlayTimer.Tick -= OverlayTimer_Tick;
        _osd?.Dispose();
        _overlay?.Dispose();
        _osd = null;
        _overlay = null;
        MediaPlayer[] players;
        lock (_playersLock) players = _activePlayers.Keys.ToArray();
        foreach (var player in players) ReleasePlayer(player);
    }
}
