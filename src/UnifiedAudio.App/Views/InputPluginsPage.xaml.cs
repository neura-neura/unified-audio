using UnifiedAudio.Helpers;
using UnifiedAudio.Services;
using UnifiedAudio.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using CoreAudioEndpoint = UnifiedAudio.Core.Audio.AudioEndpointDescriptor;
using CoreEndpointAvailability = UnifiedAudio.Core.Models.EndpointAvailability;
using CoreEndpointFlow = UnifiedAudio.Core.Models.EndpointFlow;
using CoreEndpointKind = UnifiedAudio.Core.Audio.EndpointKind;
using CoreEndpointSelectionPolicy = UnifiedAudio.Core.Audio.EndpointSelectionPolicy;

namespace UnifiedAudio.Views;

public sealed partial class InputPluginsPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private EngineSnapshot? _snapshot;
    private EnginePluginInventory _plugins = new([], []);
    private bool _busy;

    public InputPluginsPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("InputAndPlugins");
        RefreshButtonText.Text = Loc.Get("RefreshDevices");
        AutomationProperties.SetName(RefreshButton, Loc.Get("RefreshDevices"));
        ScanButton.Content = Loc.Get("ScanPlugins");
        RescanButton.Content = Loc.Get("RescanPlugins");
        RetryButton.Content = Loc.Get("RetryPlugins");
        InputBox.Header = LiteralCatalog.Get("Entrada física global del motor");
        OutputBox.Header = LiteralCatalog.Get("Salida virtual final");
        FollowGlobalInputSwitch.Header = LiteralCatalog.Get("Seguir el micrófono físico predeterminado; si Windows apunta a un cable se usa esta entrada como respaldo");
        PreserveMixerOnStartCheckBox.Content = LiteralCatalog.Get("Conservar la mezcla de PC al iniciar (elección explícita; se bloquea si Windows Listen crea un loop)");
        InputBox.DisplayMemberPath = nameof(AudioDeviceInfo.Name);
        OutputBox.DisplayMemberPath = nameof(AudioDeviceInfo.Name);
        AutomationProperties.SetName(InputBox, LiteralCatalog.Get("Entrada física global del motor"));
        AutomationProperties.SetName(OutputBox, LiteralCatalog.Get("Salida virtual final"));
        AutomationProperties.SetName(FollowGlobalInputSwitch, FollowGlobalInputSwitch.Header?.ToString() ?? string.Empty);
        AutomationProperties.SetName(PreserveMixerOnStartCheckBox, PreserveMixerOnStartCheckBox.Content?.ToString() ?? string.Empty);
        AutomationProperties.SetName(StartStopButton, Loc.Get("StartEngine"));
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        SetControlsEnabled(false);
        try
        {
            if (!Controller.Engine.IsConnected) await Controller.Engine.ConnectAsync();
            _snapshot = await Controller.Engine.GetSnapshotAsync();
            var inputs = Controller.Audio.GetPhysicalCaptureDevices();
            var outputs = Controller.Audio.GetDevices(AudioFlow.Playback);
            var currentInput = InputBox.SelectedItem as AudioDeviceInfo;
            var savedGlobalInput = Controller.Settings.GlobalPhysicalInput;
            var hasSavedGlobalInput = HasReference(savedGlobalInput);
            var initializeFromSnapshot = Controller.Settings.GlobalInputMode == GlobalInputMode.Unconfigured
                                         && !hasSavedGlobalInput;
            var selectedInputId = !string.IsNullOrWhiteSpace(currentInput?.Id)
                ? currentInput.Id
                : hasSavedGlobalInput
                    ? savedGlobalInput.Id
                    : initializeFromSnapshot
                        ? _snapshot.InputDeviceId
                        : string.Empty;
            var selectedInputName = !string.IsNullOrWhiteSpace(currentInput?.Name)
                ? currentInput.Name
                : hasSavedGlobalInput
                    ? savedGlobalInput.Name
                    : initializeFromSnapshot
                        ? _snapshot.InputDeviceName
                        : string.Empty;
            var currentOutput = OutputBox.SelectedItem as AudioDeviceInfo;
            var snapshotOutput = new SavedDeviceReference
            {
                Id = _snapshot.OutputDeviceId,
                Name = _snapshot.OutputDeviceName
            };
            var snapshotOutputDevice = outputs.FirstOrDefault(device =>
                (!string.IsNullOrWhiteSpace(snapshotOutput.Id)
                 && string.Equals(device.Id, snapshotOutput.Id, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(snapshotOutput.Name)
                    && string.Equals(device.Name, snapshotOutput.Name, StringComparison.OrdinalIgnoreCase)));
            if (snapshotOutputDevice is not null)
                snapshotOutput.Name = snapshotOutputDevice.Name;

            // The engine snapshot is runtime state, not automatically a saved
            // final endpoint. A physical/default render must not block the
            // policy from suggesting the standard CABLE Input. A virtual
            // snapshot remains an existing selection and is preserved.
            var hasSavedVirtualSnapshot = HasReference(snapshotOutput)
                                          && IsVirtualRender(snapshotOutput.Name);
            var selectedOutput = currentOutput is not null
                ? new SavedDeviceReference { Id = currentOutput.Id, Name = currentOutput.Name }
                : hasSavedVirtualSnapshot
                    ? snapshotOutput
                    : null;
            InputBox.ItemsSource = inputs;
            OutputBox.ItemsSource = outputs;
            InputBox.SelectedItem = inputs.FirstOrDefault(device =>
                string.Equals(device.Id, selectedInputId, StringComparison.OrdinalIgnoreCase))
                ?? inputs.FirstOrDefault(device =>
                    string.Equals(device.Name, selectedInputName, StringComparison.OrdinalIgnoreCase));
            var outputDecision = Controller.Audio.ResolveFinalVirtualRender(outputs, selectedOutput);
            OutputBox.SelectedItem = outputDecision.Selected is null
                ? outputs.FirstOrDefault(device =>
                    string.Equals(device.Id, selectedOutput?.Id, StringComparison.OrdinalIgnoreCase))
                  ?? outputs.FirstOrDefault(device =>
                    string.Equals(device.Name, selectedOutput?.Name, StringComparison.OrdinalIgnoreCase))
                : outputs.FirstOrDefault(device =>
                    string.Equals(device.Id, outputDecision.Selected.Id, StringComparison.OrdinalIgnoreCase));
            FollowGlobalInputSwitch.IsOn = Controller.Settings.GlobalInputMode == GlobalInputMode.FollowWindowsDefault;
            PreserveMixerOnStartCheckBox.IsChecked = false;
            AutomationProperties.SetName(StartStopButton, _snapshot.Running ? Loc.Get("StopEngine") : Loc.Get("StartEngine"));
            await RefreshPluginsAsync();
            RenderSnapshot();
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetControlsEnabled(false);
        try
        {
            var inheritedSystemMixer = false;
            if (_snapshot?.Running == true)
            {
                _snapshot = await Controller.Engine.StopAsync();
            }
            else if (InputBox.SelectedItem is AudioDeviceInfo input
                     && OutputBox.SelectedItem is AudioDeviceInfo output)
            {
                inheritedSystemMixer = _snapshot?.MixerMode != (int)EngineMixerMode.Voice;
                _snapshot = await Controller.Engine.StartAsync(
                    input.Name,
                    output.Name,
                    double.IsNaN(BufferBox.Value) ? 0 : (int)BufferBox.Value,
                    input.Id,
                    output.Id,
                    preserveMixer: PreserveMixerOnStartCheckBox.IsChecked == true);
                Controller.CommitGlobalPhysicalInput(
                    new SavedDeviceReference { Id = input.Id, Name = input.Name },
                    FollowGlobalInputSwitch.IsOn);
            }
            else
            {
                throw new InvalidOperationException(LiteralCatalog.Get("Selecciona una entrada y una salida disponibles."));
            }
            RenderSnapshot();
            if (_snapshot?.FeedbackLoopGuarded == true)
            {
                StatusBar.Title = LiteralCatalog.Get("Protección contra loop digital");
                StatusBar.Message = _snapshot.FeedbackLoopDescription;
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.IsOpen = true;
            }
            else if (inheritedSystemMixer
                     && _snapshot?.MixerMode == (int)EngineMixerMode.Voice
                     && PreserveMixerOnStartCheckBox.IsChecked != true)
            {
                StatusBar.Title = LiteralCatalog.Get("Inicio seguro en Voz");
                StatusBar.Message = LiteralCatalog.Get("La mezcla System/Both anterior no se reactivó al abrir endpoints. Para mezclar audio del PC, selecciónalo explícitamente en Mezclador.");
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.IsOpen = true;
            }
            else
            {
                StatusBar.IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: string command }) return;
        _busy = true;
        SetControlsEnabled(false);
        try
        {
            _snapshot = await Controller.Engine.ScanAsync(command);
            await RefreshPluginsAsync();
            RenderSnapshot();
            StatusBar.Title = "VST3";
            StatusBar.Message = LiteralCatalog.Get("La operación de escaneo se ejecuta en procesos aislados.");
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private void RenderSnapshot()
    {
        if (_snapshot is null) return;
        StartStopButton.Content = _snapshot.Running ? Loc.Get("StopEngine") : Loc.Get("StartEngine");
        AutomationProperties.SetName(StartStopButton, StartStopButton.Content?.ToString() ?? string.Empty);
        PluginSummary.Text = _snapshot.Scanning
            ? string.Format(LiteralCatalog.Get("Escaneando… {0} plugins disponibles; {1} omitidos."), _plugins.Catalog.Count, _snapshot.SkippedPluginCount)
            : string.Format(LiteralCatalog.Get("{0} plugins en la cadena; {1} disponibles; {2} omitidos."), _plugins.Chain.Count, _plugins.Catalog.Count, _snapshot.SkippedPluginCount);
        SkipButton.IsEnabled = _snapshot.Scanning;
    }

    private async Task RefreshPluginsAsync()
    {
        _plugins = await Controller.Engine.GetPluginsAsync();
        var folders = await Controller.Engine.GetPluginFoldersAsync();
        ApplyPluginFilter();
        ChainList.ItemsSource = _plugins.Chain;
        PluginFoldersList.ItemsSource = folders.Folders;
    }

    private void ApplyPluginFilter()
    {
        var query = PluginSearchBox.Text?.Trim() ?? string.Empty;
        CatalogBox.ItemsSource = _plugins.Catalog
            .Where(plugin => string.IsNullOrEmpty(query)
                || plugin.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || plugin.Manufacturer.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(plugin => plugin.Manufacturer, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (CatalogBox.Items.Count > 0 && CatalogBox.SelectedIndex < 0)
        {
            CatalogBox.SelectedIndex = 0;
        }
    }

    private void PluginSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyPluginFilter();

    private async void AddPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogBox.SelectedItem is EnginePluginDescriptor plugin)
        {
            await RunPluginMutationAsync(() => Controller.Engine.AddPluginAsync(plugin.Id), int.MaxValue);
        }
    }

    private async void RemovePluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainList.SelectedItem is EnginePluginEntry plugin)
        {
            await RunPluginMutationAsync(() => Controller.Engine.RemovePluginAsync(plugin.Index), plugin.Index - 1);
        }
    }

    private async void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainList.SelectedItem is EnginePluginEntry { Index: > 0 } plugin)
        {
            await RunPluginMutationAsync(() => Controller.Engine.MovePluginAsync(plugin.Index, plugin.Index - 1), plugin.Index - 1);
        }
    }

    private async void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainList.SelectedItem is EnginePluginEntry plugin && plugin.Index + 1 < _plugins.Chain.Count)
        {
            await RunPluginMutationAsync(() => Controller.Engine.MovePluginAsync(plugin.Index, plugin.Index + 1), plugin.Index + 1);
        }
    }

    private async void BypassButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainList.SelectedItem is EnginePluginEntry plugin)
        {
            await RunPluginMutationAsync(
                () => Controller.Engine.SetPluginBypassAsync(plugin.Index, !plugin.Bypassed),
                plugin.Index);
        }
    }

    private async void OpenEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChainList.SelectedItem is not EnginePluginEntry plugin) return;
        try
        {
            await Controller.Engine.OpenPluginEditorAsync(plugin.Index);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var window = App.Instance.MainAppWindow;
        if (window is null) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await RunPluginMutationAsync(() => Controller.Engine.AddPluginFolderAsync(folder.Path), -1);
    }

    private async void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (PluginFoldersList.SelectedItem is string folder)
        {
            await RunPluginMutationAsync(() => Controller.Engine.RemovePluginFolderAsync(folder), -1);
        }
    }

    private async Task RunPluginMutationAsync(Func<Task<EngineSnapshot>> mutation, int preferredIndex)
    {
        if (_busy) return;
        _busy = true;
        SetControlsEnabled(false);
        try
        {
            _snapshot = await mutation();
            await RefreshPluginsAsync();
            RenderSnapshot();
            if (_plugins.Chain.Count > 0)
            {
                ChainList.SelectedIndex = Math.Clamp(preferredIndex, 0, _plugins.Chain.Count - 1);
            }
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        StartStopButton.IsEnabled = enabled;
        ScanButton.IsEnabled = enabled;
        RescanButton.IsEnabled = enabled;
        RetryButton.IsEnabled = enabled;
        InputBox.IsEnabled = enabled;
        OutputBox.IsEnabled = enabled;
        FollowGlobalInputSwitch.IsEnabled = enabled;
        PreserveMixerOnStartCheckBox.IsEnabled = enabled;
        BufferBox.IsEnabled = enabled;
        PluginSearchBox.IsEnabled = enabled;
        CatalogBox.IsEnabled = enabled;
        AddPluginButton.IsEnabled = enabled;
        ChainList.IsEnabled = enabled;
    }

    private void ShowError(Exception exception)
    {
        StatusBar.Title = Loc.Get("EngineUnavailable");
        StatusBar.Message = exception.Message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }

    private static bool HasReference(SavedDeviceReference reference) =>
        !string.IsNullOrWhiteSpace(reference.Id) || !string.IsNullOrWhiteSpace(reference.Name);

    private static bool IsVirtualRender(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && CoreEndpointSelectionPolicy.Classify(new CoreAudioEndpoint
        {
            Name = name,
            Flow = CoreEndpointFlow.Render,
            Availability = CoreEndpointAvailability.Disconnected
        }) == CoreEndpointKind.VirtualCableRender;
}
