using System.Diagnostics;
using UnifiedAudio.Helpers;
using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace UnifiedAudio.Views;

public sealed partial class DiagnosticsPage : Page
{
    private AppController Controller => App.Instance.Controller;

    public DiagnosticsPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("Diagnostics");
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            if (!Controller.Engine.IsConnected) await Controller.Engine.ConnectAsync();
            var snapshot = await Controller.Engine.GetSnapshotAsync();
            EngineState.Text = LiteralCatalog.Get(snapshot.Running ? "Activo" : "Inactivo");
            MuteState.Text = snapshot.MutedRequested
                ? LiteralCatalog.Get(snapshot.MuteSettled ? "Muteado, cero digital" : "Muteando")
                : LiteralCatalog.Get("Desmuteado");
            PluginState.Text = string.Format(LiteralCatalog.Get("{0} en cadena, {1} omitidos, escaneo {2}"),
                snapshot.PluginCount, snapshot.SkippedPluginCount, LiteralCatalog.Get(snapshot.Scanning ? "activo" : "inactivo"));
            InputPeak.Text = ToDb(snapshot.InputPeak);
            VoicePeak.Text = ToDb(snapshot.VoicePeak);
            SystemPeak.Text = ToDb(snapshot.SystemPeak);
            OutputPeak.Text = ToDb(snapshot.OutputPeak);
            EndpointState.Text = string.Format(LiteralCatalog.Get("Entrada: {0} · Salida: {1} · Sistema: {2}"), snapshot.InputDeviceName, snapshot.OutputDeviceName, snapshot.SystemCaptureDeviceName);
            EndpointIds.Text = string.Format(LiteralCatalog.Get("Entrada: {0} · Salida: {1}"), snapshot.InputDeviceId, snapshot.OutputDeviceId);
            RatesState.Text = $"{snapshot.CaptureSampleRate:F0} Hz → {snapshot.GraphSampleRate:F0} Hz";
            QueueState.Text = $"{snapshot.QueuedCaptureFrames} frames";
            XrunState.Text = $"{snapshot.Underruns} underruns / {snapshot.Overruns} overruns";
            var feedbackState = snapshot.FeedbackLoopDetected
                ? (string.IsNullOrWhiteSpace(snapshot.FeedbackLoopDescription)
                    ? LiteralCatalog.Get("Ruta de feedback detectada")
                    : snapshot.FeedbackLoopDescription)
                : snapshot.ListenRouteActive
                    ? LiteralCatalog.Get("Windows Listen está activo en la captura de cable, pero no apunta a la fuente de sistema seleccionada.")
                    : LiteralCatalog.Get("No detectada");
            var probeState = snapshot.FeedbackProbeUnknown
                ? "Probe Listen/consumidores: desconocido o incompleto (System/Both protegido)"
                : snapshot.FeedbackProbeAvailable
                    ? "Probe Listen/consumidores: disponible"
                    : "Probe Listen/consumidores: no disponible";
            if (snapshot.FinalCaptureConsumerDetected)
                probeState += $"; consumidores CABLE Output: {snapshot.FinalCaptureConsumerSummary}";
            if (!string.IsNullOrWhiteSpace(snapshot.FinalCaptureEndpointId))
                probeState += $"; capture final: {snapshot.FinalCaptureEndpointName} [{snapshot.FinalCaptureEndpointId}]";
            FeedbackState.Text = feedbackState + Environment.NewLine + probeState;
            FeedbackGuardState.Text = snapshot.FeedbackLoopGuarded
                ? LiteralCatalog.Get("Captura de sistema detenida; Voz segura")
                : LiteralCatalog.Get("No aplicada");
            LogText.Text = ReadLogs();
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            StatusBar.Title = Loc.Get("EngineUnavailable");
            StatusBar.Message = ex.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = Controller.Log.DirectoryPath;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(LogText.Text ?? string.Empty);
        Clipboard.SetContent(package);
        StatusBar.Title = "Logs copiados";
        StatusBar.Message = LiteralCatalog.Get("Los logs visibles están en el portapapeles.");
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.IsOpen = true;
    }

    private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Limpiar logs",
            Content = LiteralCatalog.Get("Se vaciarán los logs locales de UnifiedAudio. Esta acción no cambia perfiles ni audio."),
            PrimaryButtonText = "Limpiar",
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var path in LogPaths())
        {
            try { if (File.Exists(path)) File.WriteAllText(path, string.Empty); }
            catch (Exception ex) { Controller.Log.Warn($"Could not clear log '{path}': {ex.Message}"); }
        }
        LogText.Text = ReadLogs();
    }

    private void OpenRecordingPanelButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "control.exe",
            Arguments = "mmsys.cpl,,1",
            UseShellExecute = true
        });

    private string ReadLogs()
    {
        var parts = new List<string>();
        foreach (var path in LogPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                if (text.Length > 200_000) text = text[^200_000..];
                parts.Add($"===== {path} ====={Environment.NewLine}{text}");
            }
            catch (Exception ex)
            {
                parts.Add($"===== {path} ====={Environment.NewLine}{ex.Message}");
            }
        }
        return string.Join(Environment.NewLine, parts);
    }

    private IEnumerable<string> LogPaths()
    {
        yield return Controller.Log.FilePath;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "UnifiedAudio", "logs", "engine.log");
    }

    private static string ToDb(float value) => value <= 0f ? "−∞ dBFS" : $"{20 * Math.Log10(value):F1} dBFS";
}
