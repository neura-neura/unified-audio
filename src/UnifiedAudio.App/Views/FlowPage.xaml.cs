using System.Collections.ObjectModel;
using AudioProfilesModel = UnifiedAudio.Models.AudioProfile;
using UnifiedAudio.Helpers;
using UnifiedAudio.Models;
using UnifiedAudio.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnifiedAudio.Views;

public sealed partial class FlowPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private DispatcherQueueTimer? _timer;
    private bool _refreshing;

    public FlowPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("Flow");
        SubtitleText.Text = LiteralCatalog.Get("Entrada → VST3 → Mute → Mezcla → Salida virtual. Cada tarjeta refleja el estado que reporta el motor.");
    }

    public ObservableCollection<FlowNodeCard> Nodes { get; } = [];

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += Timer_Tick;
        _timer.Start();
        await RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer = null;
        }
    }

    private async void Timer_Tick(DispatcherQueueTimer sender, object args) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (!Controller.Engine.IsConnected)
            {
                await Controller.Engine.ConnectAsync();
            }
            var snapshot = await Controller.Engine.GetSnapshotAsync();
            var inputName = string.IsNullOrWhiteSpace(snapshot.InputDeviceName)
                ? LiteralCatalog.Get("Sin entrada abierta")
                : $"{snapshot.InputDeviceName} · {snapshot.InputDeviceId}";
            var outputName = string.IsNullOrWhiteSpace(snapshot.OutputDeviceName)
                ? LiteralCatalog.Get("Sin salida abierta")
                : $"{snapshot.OutputDeviceName} · {snapshot.OutputDeviceId}";

            Nodes.Clear();
            Nodes.Add(new(LiteralCatalog.Get("Entrada"), "\uE720", LiteralCatalog.Get(snapshot.Running ? "Activa" : "Inactiva"), inputName, snapshot.InputPeak, "inputPlugins", $"{LiteralCatalog.Get("Entrada")}, {inputName}", LiteralCatalog.Get("Nivel de entrada")));
            Nodes.Add(new("VST3", "\uE945", string.Format(LiteralCatalog.Get("{0} plugins"), snapshot.PluginCount), LiteralCatalog.Get(snapshot.Scanning ? "Escaneo en curso" : "Cadena lista"), snapshot.VoicePeak, "inputPlugins", string.Format(LiteralCatalog.Get("Cadena VST3, {0} plugins"), snapshot.PluginCount), LiteralCatalog.Get("Nivel tras plugins y gate")));
            Nodes.Add(new(LiteralCatalog.Get("Mute"), "\uE74F", LiteralCatalog.Get(snapshot.MutedRequested ? "Muteado" : "Desmuteado"), LiteralCatalog.Get(snapshot.MuteSettled ? "Silencio digital establecido" : "Gate disponible"), snapshot.VoicePeak, "muteFeedback", LiteralCatalog.Get(snapshot.MutedRequested ? "Micrófono muteado" : "Micrófono desmuteado"), LiteralCatalog.Get("Nivel de voz transmitida")));
            Nodes.Add(new(LiteralCatalog.Get("Audio del sistema/apps"), "\uE8D6",
                LiteralCatalog.Get(snapshot.SystemCaptureRunning ? "Loopback activo" : "Inactivo"),
                snapshot.ProcessFilterEnabled
                    ? string.Format(LiteralCatalog.Get("{0} · apps {1}/{2}"), snapshot.SystemCaptureDeviceName, snapshot.ActiveProcessCaptureCount, snapshot.ConfiguredProcessRuleCount)
                    : string.IsNullOrWhiteSpace(snapshot.SystemCaptureDeviceName) ? LiteralCatalog.Get("Sin endpoint") : snapshot.SystemCaptureDeviceName,
                snapshot.SystemPeak, "mixer", LiteralCatalog.Get("Audio del sistema y aplicaciones"), LiteralCatalog.Get("Nivel de sistema")));
            var mixerMode = LiteralCatalog.Get(((EngineMixerMode)Math.Clamp(snapshot.MixerMode, 0, 2)).ToString());
            Nodes.Add(new(LiteralCatalog.Get("Mezcla"), "\uE9E9", LiteralCatalog.Get(snapshot.Running ? "Procesando" : "Detenida"),
                mixerMode, snapshot.OutputPeak, "mixer", LiteralCatalog.Get("Mezclador y enrutamiento"), LiteralCatalog.Get("Nivel de mezcla")));
            Nodes.Add(new(LiteralCatalog.Get("Salida virtual"), "\uE7F5", LiteralCatalog.Get(snapshot.Running ? "Activa" : "Inactiva"), outputName, snapshot.OutputPeak, "inputPlugins", $"{LiteralCatalog.Get("Salida virtual")}, {outputName}", LiteralCatalog.Get("Nivel de salida")));

            StatusBar.Title = snapshot.Running ? Loc.Get("EngineActive") : Loc.Get("EngineIdle");
            StatusBar.Message = snapshot.Scanning
                ? string.Format(LiteralCatalog.Get("Escaneando VST3. Plugins omitidos: {0}."), snapshot.SkippedPluginCount)
                : string.Format(LiteralCatalog.Get("Plugins: {0}. Omitidos: {1}."), snapshot.PluginCount, snapshot.SkippedPluginCount);
            StatusBar.Severity = snapshot.Running ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            if (snapshot.FeedbackLoopGuarded || snapshot.FeedbackLoopDetected)
            {
                StatusBar.Title = LiteralCatalog.Get(snapshot.FeedbackLoopGuarded
                    ? "Protección contra loop digital"
                    : "Ruta de feedback detectada");
                StatusBar.Message = string.IsNullOrWhiteSpace(snapshot.FeedbackLoopDescription)
                    ? LiteralCatalog.Get("Windows Listen puede devolver la salida final a la captura de sistema. Usa Voz o desactiva Listen antes de mezclar PC.")
                    : snapshot.FeedbackLoopDescription;
                StatusBar.Severity = InfoBarSeverity.Warning;
            }
        }
        catch (Exception ex)
        {
            StatusBar.Title = Loc.Get("EngineUnavailable");
            StatusBar.Message = ex.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            Nodes.Clear();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Node_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string route })
        {
            App.Instance.MainAppWindow?.NavigateToTag(route);
        }
    }
}

public sealed class FlowNodeCard
{
    public FlowNodeCard(
        string name,
        string glyph,
        string status,
        string detail,
        float level,
        string route,
        string accessibleName,
        string meterName)
    {
        Name = name;
        Glyph = glyph;
        Status = status;
        Detail = detail;
        Level = level;
        Route = route;
        AccessibleName = accessibleName;
        MeterName = meterName;
    }

    public string Name { get; set; }
    public string Glyph { get; set; }
    public string Status { get; set; }
    public string Detail { get; set; }
    public float Level { get; set; }
    public string Route { get; set; }
    public string AccessibleName { get; set; }
    public string MeterName { get; set; }
}
