using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnifiedAudio.Helpers;
using UnifiedAudio.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace UnifiedAudio.Views;

public sealed class AppSessionRow : INotifyPropertyChanged
{
    private bool _included;
    private double _gainPercent;
    private float _peak;
    private bool _active;
    private bool _sessionMuted;
    private long _processId;

    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public bool Included { get => _included; set => Set(ref _included, value); }
    public double GainPercent { get => _gainPercent; set => Set(ref _gainPercent, Math.Clamp(value, 0, 400)); }
    public float Peak { get => _peak; set { if (Set(ref _peak, value)) OnPropertyChanged(nameof(MeterAccessibleName)); } }
    public bool Active { get => _active; set { if (Set(ref _active, value)) NotifyState(); } }
    public bool SessionMuted { get => _sessionMuted; set { if (Set(ref _sessionMuted, value)) NotifyState(); } }
    public long ProcessId { get => _processId; set { if (Set(ref _processId, value)) OnPropertyChanged(nameof(DetailText)); } }

    public string StateText => LiteralCatalog.Get(Active ? (SessionMuted ? "Activa · silenciada en Windows" : "Activa") : "No está emitiendo ahora");
    public string DetailText => ProcessId > 0
        ? $"{ProcessName} · PID {ProcessId} · {ExecutablePath}"
        : $"{LiteralCatalog.Get("Regla guardada")} · {ExecutablePath}";
    public string AccessibleName => string.Format(LiteralCatalog.Get("Incluir {0}"), DisplayName);
    public string MeterAccessibleName => string.Format(LiteralCatalog.Get("Nivel de {0}: {1}"), DisplayName, ToDb(Peak));
    public string GainAccessibleName => string.Format(LiteralCatalog.Get("Volumen de {0} en la mezcla"), DisplayName);
    public string GainHeader => LiteralCatalog.Get("Volumen en la mezcla");

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DetailText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string ToDb(float value) => value <= 0 ? LiteralCatalog.Get("silencio") : $"{20 * Math.Log10(value):F1} dBFS";
}

public sealed partial class MixerPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private DispatcherQueueTimer? _timer;
    private bool _refreshing;
    private bool _refreshingSessions;
    private int _telemetryTicks;
    private IReadOnlyList<float> _spectrum = [];
    public ObservableCollection<AppSessionRow> SessionRows { get; } = [];

    public MixerPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("Mixer");
        ModeButtons.ItemsSource = new[]
        {
            LiteralCatalog.Get("Voz"),
            LiteralCatalog.Get("Audio del PC"),
            LiteralCatalog.Get("Ambos")
        };
        SystemDeviceBox.DisplayMemberPath = nameof(EngineEndpointDescriptor.Name);
        ProcessFilterModeBox.ItemsSource = new[]
        {
            LiteralCatalog.Get("Solo las marcadas"),
            LiteralCatalog.Get("Todas salvo las desmarcadas")
        };
        UpdateGainLabels();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_timer is null) return;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _timer = null;
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        await RefreshTelemetryAsync();
        if (++_telemetryTicks % 8 == 0) await RefreshSessionsAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!Controller.Engine.IsConnected) await Controller.Engine.ConnectAsync();
            var devices = await Controller.Engine.GetDevicesAsync();
            var snapshot = await Controller.Engine.GetSnapshotAsync();
            SystemDeviceBox.ItemsSource = devices.OutputEndpoints;
            var selectedEndpoint = !string.IsNullOrWhiteSpace(snapshot.SystemCaptureDeviceId)
                ? devices.OutputEndpoints.FirstOrDefault(endpoint =>
                    string.Equals(endpoint.Id, snapshot.SystemCaptureDeviceId, StringComparison.OrdinalIgnoreCase))
                : devices.OutputEndpoints.Count(endpoint =>
                    string.Equals(endpoint.Name, snapshot.SystemCaptureDeviceName, StringComparison.OrdinalIgnoreCase)) == 1
                    ? devices.OutputEndpoints.First(endpoint =>
                        string.Equals(endpoint.Name, snapshot.SystemCaptureDeviceName, StringComparison.OrdinalIgnoreCase))
                    : null;
            SystemDeviceBox.SelectedItem = selectedEndpoint;
            ModeButtons.SelectedIndex = Math.Clamp(snapshot.MixerMode, 0, 2);
            VoiceSlider.Value = snapshot.VoiceGain * 100.0;
            SystemSlider.Value = snapshot.SystemGain * 100.0;
            DuckingSwitch.IsOn = snapshot.DuckingEnabled;
            DuckAmountBox.Value = snapshot.DuckAmount * 100.0;
            ThresholdBox.Value = snapshot.DuckThresholdDb;
            AttackBox.Value = snapshot.DuckAttackMs;
            HoldBox.Value = snapshot.DuckHoldMs;
            ReleaseBox.Value = snapshot.DuckReleaseMs;
            ProcessFilterSwitch.IsOn = snapshot.ProcessFilterEnabled;
            ProcessFilterModeBox.SelectedIndex = snapshot.ProcessFilterExclusionMode ? 1 : 0;
            await RefreshSessionsAsync();
            RenderTelemetry(snapshot);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        try
        {
            var mode = (EngineMixerMode)Math.Clamp(ModeButtons.SelectedIndex, 0, 2);
            var selectedEndpoint = SystemDeviceBox.SelectedItem as EngineEndpointDescriptor;
            if (mode != EngineMixerMode.Voice && selectedEndpoint is null)
                throw new InvalidOperationException(LiteralCatalog.Get("Selecciona un endpoint de sistema identificado para mezclar audio del PC."));
            var snapshot = await Controller.Engine.ConfigurePipelineAsync(
                null,
                new EngineMixerConfiguration(
                    mode,
                    selectedEndpoint?.Name ?? string.Empty,
                    selectedEndpoint?.Id ?? string.Empty,
                    (float)(VoiceSlider.Value / 100.0),
                    (float)(SystemSlider.Value / 100.0),
                    DuckingSwitch.IsOn,
                    (float)(SafeNumber(DuckAmountBox, 55) / 100.0),
                    (float)SafeNumber(ThresholdBox, -36),
                    (int)SafeNumber(AttackBox, 20),
                    (int)SafeNumber(HoldBox, 200),
                    (int)SafeNumber(ReleaseBox, 280),
                    ProcessFilterSwitch.IsOn,
                    ProcessFilterModeBox.SelectedIndex == 1,
                    SessionRows.Select(row =>
                        new EngineProcessMixRule(row.Key, row.DisplayName,
                            (float)(row.GainPercent / 100.0), !row.Included)).ToArray()),
                null,
                null);
            RenderTelemetry(snapshot);
            StatusBar.Title = snapshot.SystemCaptureRunning || mode == EngineMixerMode.Voice
                ? LiteralCatalog.Get("Mezclador activo")
                : LiteralCatalog.Get("Captura de sistema inactiva");
            StatusBar.Message = mode == EngineMixerMode.Voice
                ? LiteralCatalog.Get("Solo la voz procesada llega al endpoint final.")
                : snapshot.ProcessFilterEnabled
                    ? string.Format(LiteralCatalog.Get("Captura por aplicación: {0} activa(s) de {1} configurada(s)."), snapshot.ActiveProcessCaptureCount, snapshot.ConfiguredProcessRuleCount)
                    : string.Format(LiteralCatalog.Get("Loopback activo en {0}."), snapshot.SystemCaptureDeviceName);
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private async Task RefreshTelemetryAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            RenderTelemetry(await Controller.Engine.GetSnapshotAsync());
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshSessionsAsync()
    {
        if (_refreshingSessions || !Controller.Engine.IsConnected) return;
        _refreshingSessions = true;
        try
        {
            var selectedEndpoint = SystemDeviceBox.SelectedItem as EngineEndpointDescriptor;
            var response = await Controller.Engine.GetAudioSessionsAsync(
                selectedEndpoint?.Name ?? string.Empty,
                selectedEndpoint?.Id ?? string.Empty);
            var current = SessionRows
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in response.Sessions)
            {
                returnedKeys.Add(session.Key);
                if (!current.TryGetValue(session.Key, out var row))
                {
                    row = new AppSessionRow
                    {
                        Key = session.Key,
                        DisplayName = session.DisplayName,
                        ProcessName = session.ProcessName,
                        ExecutablePath = session.ExecutablePath,
                        Included = session.Included,
                        GainPercent = session.ConfiguredGain * 100.0
                    };
                    SessionRows.Add(row);
                }
                row.ProcessId = session.ProcessId;
                row.Active = session.Active;
                row.SessionMuted = session.SessionMuted;
                row.Peak = session.Peak;
            }

            for (var index = SessionRows.Count - 1; index >= 0; index--)
                if (!returnedKeys.Contains(SessionRows[index].Key)) SessionRows.RemoveAt(index);

            var activeCount = SessionRows.Count(row => row.Active);
            SessionSummaryText.Text = string.Format(LiteralCatalog.Get("{0} activa(s) · {1} detectada(s) · máximo 32 capturas activas"), activeCount, SessionRows.Count);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _refreshingSessions = false;
        }
    }

    private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e) => await RefreshSessionsAsync();

    private async void SystemDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await RefreshSessionsAsync();
    }

    private void RenderTelemetry(EngineSnapshot snapshot)
    {
        VoiceMeter.Value = snapshot.VoicePeak;
        SystemMeter.Value = snapshot.SystemPeak;
        OutputMeter.Value = snapshot.OutputPeak;
        _spectrum = snapshot.Spectrum;
        RenderSpectrum();
        var source = snapshot.ProcessFilterEnabled
            ? string.Format(LiteralCatalog.Get("Apps {0}/{1}"), snapshot.ActiveProcessCaptureCount, snapshot.ConfiguredProcessRuleCount)
            : LiteralCatalog.Get("Endpoint completo");
        TelemetryText.Text = string.Format(LiteralCatalog.Get("Voz {0} · PC {1} · Final {2} · {3} · Duck {4:P0} · XRuns {5}/{6}"),
            ToDb(snapshot.VoicePeak), ToDb(snapshot.SystemPeak), ToDb(snapshot.OutputPeak), source,
            snapshot.DuckingGain, snapshot.Underruns, snapshot.Overruns);

        if (snapshot.FeedbackLoopGuarded)
        {
            StatusBar.Title = LiteralCatalog.Get("Protección contra loop digital");
            StatusBar.Message = string.IsNullOrWhiteSpace(snapshot.FeedbackLoopDescription)
                ? LiteralCatalog.Get("La captura de sistema se detuvo y el mezclador volvió a Voz para evitar feedback digital.")
                : snapshot.FeedbackLoopDescription;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.IsOpen = true;
        }
        else if (snapshot.FeedbackLoopDetected)
        {
            StatusBar.Title = LiteralCatalog.Get("Ruta de feedback detectada");
            StatusBar.Message = string.IsNullOrWhiteSpace(snapshot.FeedbackLoopDescription)
                ? LiteralCatalog.Get("Windows Listen puede devolver la salida final a la captura de sistema. Usa Voz o desactiva Listen antes de mezclar PC.")
                : snapshot.FeedbackLoopDescription;
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.IsOpen = true;
        }
    }

    private void SpectrumCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderSpectrum();

    private void RenderSpectrum()
    {
        if (SpectrumLine is null || SpectrumCanvas is null || _spectrum.Count == 0) return;
        var width = SpectrumCanvas.ActualWidth;
        var height = SpectrumCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var points = new PointCollection();
        for (var index = 0; index < _spectrum.Count; index++)
        {
            var x = _spectrum.Count == 1 ? 0 : width * index / (_spectrum.Count - 1);
            var y = height * (1.0 - Math.Clamp(_spectrum[index], 0.0f, 1.0f));
            points.Add(new Point(x, y));
        }
        SpectrumLine.Points = points;
    }

    private void GainSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateGainLabels();

    private void UpdateGainLabels()
    {
        if (VoiceValueText is null || SystemValueText is null) return;
        VoiceValueText.Text = $"{VoiceSlider.Value:F0} %";
        SystemValueText.Text = $"{SystemSlider.Value:F0} %";
    }

    private static double SafeNumber(NumberBox box, double fallback) => double.IsNaN(box.Value) ? fallback : box.Value;
    private static string ToDb(float value) => value <= 0 ? "−∞ dBFS" : $"{20 * Math.Log10(value):F1} dBFS";

    private void ShowError(Exception ex)
    {
        StatusBar.Title = Loc.Get("EngineUnavailable");
        StatusBar.Message = ex.Message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }
}
