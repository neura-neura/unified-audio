using System.Collections.ObjectModel;
using UnifiedAudio.Helpers;
using UnifiedAudio.Models;
using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace UnifiedAudio.Views;

public sealed class ProfileApplicationRuleRow
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double GainPercent { get; set; } = 100.0;
    public bool Excluded { get; set; }
}

public sealed partial class EditProfilePage : Page
{
    private AppController Controller => App.Instance.Controller;
    private AudioProfile _profile = new();
    private bool _isNew = true;
    private bool _ready;
    public ObservableCollection<ProfileApplicationRuleRow> ProfileApplicationRules { get; } = [];

    public EditProfilePage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        AutomationProperties.SetName(BackButton, Loc.Get("Back"));
        ToolTipService.SetToolTip(BackButton, Loc.Get("Back"));
        AutomationProperties.SetName(CancelButton, Loc.Get("Cancel"));
        NameLabel.Text = Loc.Get("Name");
        IconLabel.Text = Loc.Get("Icon");
        OutputLabel.Text = Loc.Get("WindowsOutput");
        InputLabel.Text = Loc.Get("WindowsInput");
        ConfigureGlobalInputButton.Content = Loc.Get("ConfigureGlobalInput");
        LegacyInputNotice.Text = Loc.Get("LegacyInputNotice");
        ShortcutLabel.Text = Loc.Get("Shortcut");
        ShortcutHelp.Text = Loc.Get("ShortcutHelp");
        HotkeyBox.PlaceholderText = Loc.Get("PressShortcut");
        ClearHotkeyButton.Content = Loc.Get("ClearShortcut");
        AdvancedExpander.Header = Loc.Get("Advanced");
        AdvancedHelp.Text = Loc.Get("AdvancedProfileHelp");
        WindowsInputDefaultsSwitch.Header = LiteralCatalog.Get("Cambiar también la entrada predeterminada de Windows para este perfil");
        ProfileInputOverrideSwitch.Header = LiteralCatalog.Get("Sobrescribir la entrada física global para este perfil");
        AdvancedRolesSwitch.Header = Loc.Get("UseAdvancedRoles");
        AdvancedRolesHint.Text = Loc.Get("AdvancedRolesHint");
        OutputConsoleLabel.Text = Loc.Format("OutputRole", Loc.Get("RoleDefault"));
        OutputMultimediaLabel.Text = Loc.Format("OutputRole", Loc.Get("RoleMedia"));
        OutputCommunicationsLabel.Text = Loc.Format("OutputRole", Loc.Get("RoleCalls"));
        InputConsoleLabel.Text = Loc.Format("InputRole", Loc.Get("RoleDefault"));
        InputMultimediaLabel.Text = Loc.Format("InputRole", Loc.Get("RoleMedia"));
        InputCommunicationsLabel.Text = Loc.Format("InputRole", Loc.Get("RoleCalls"));
        SaveButton.Content = Loc.Get("Save");
        CancelButton.Content = Loc.Get("Cancel");
        AutomationProperties.SetName(NameBox, Loc.Get("Name"));
        AutomationProperties.SetName(OutputBox, Loc.Get("Output"));
        AutomationProperties.SetName(InputBox, Loc.Get("Input"));
        AutomationProperties.SetName(GlobalInputSummary, LiteralCatalog.Get("Entrada física global"));
        AutomationProperties.SetName(ConfigureGlobalInputButton, Loc.Get("ConfigureGlobalInput"));
        AutomationProperties.SetName(LegacyInputNotice, Loc.Get("LegacyInputNotice"));
        AutomationProperties.SetName(WindowsInputDefaultsSwitch, LiteralCatalog.Get("Entrada predeterminada de Windows por perfil"));
        AutomationProperties.SetName(ProfileInputOverrideSwitch, LiteralCatalog.Get("Override de entrada física por perfil"));
        AutomationProperties.SetName(HotkeyBox, Loc.Get("Shortcut"));
        AutomationProperties.SetName(OutputConsoleBox, OutputConsoleLabel.Text);
        AutomationProperties.SetName(OutputMultimediaBox, OutputMultimediaLabel.Text);
        AutomationProperties.SetName(OutputCommunicationsBox, OutputCommunicationsLabel.Text);
        AutomationProperties.SetName(InputConsoleBox, InputConsoleLabel.Text);
        AutomationProperties.SetName(InputMultimediaBox, InputMultimediaLabel.Text);
        AutomationProperties.SetName(InputCommunicationsBox, InputCommunicationsLabel.Text);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var existing = e.Parameter as string;
        var found = Controller.Profiles.FirstOrDefault(p => p.Id == existing);
        _isNew = found is null;
        _profile = found?.Clone() ?? new AudioProfile();
        if (_profile.InputMode == ProfileInputMode.FollowGlobal)
            _profile.ConfigureWindowsInputDefaults = false;
        TitleText.Text = _isNew ? Loc.Get("NewProfile") : Loc.Get("EditProfile");
        NameBox.Text = _profile.Name;
        BindIcons();
        BindDevices();
        HotkeyBox.Text = HotkeyFormatter.ToDisplay(_profile.Hotkey);
        ErrorText.Text = string.Empty;
        _ready = false;
        WindowsInputDefaultsSwitch.IsOn = _profile.InputMode != ProfileInputMode.FollowGlobal
                                          && _profile.ConfigureWindowsInputDefaults;
        InternalPipelineSwitch.IsOn = _profile.ConfigureInternalPipeline;
        ProfileInputOverrideSwitch.IsOn = _profile.InputMode == ProfileInputMode.ProfileOverride;
        UseDefaultInternalInputSwitch.IsOn = _profile.UseDefaultInternalInput;
        EngineBufferBox.Value = _profile.EngineBufferSize;
        InternalMuteBox.ItemsSource = Enum.GetValues<InternalMuteState>();
        InternalMuteBox.SelectedItem = _profile.InternalMute;
        ExternalMuteSwitch.IsOn = _profile.ConfigureExternalEndpoints;
        ExternalMuteAllSwitch.IsOn = _profile.ExternalMuteAllRecordingDevices;
        ForceExternalMuteSwitch.IsOn = _profile.ForceExternalMuteState;
        ExternalVolumeLockSwitch.IsOn = _profile.ExternalVolumeLockEnabled;
        ExternalVolumeBox.Value = _profile.ExternalVolumeScalar * 100.0;
        ProfilePluginChainSwitch.IsOn = _profile.ConfigurePluginChain;
        UpdateProfilePluginChainSummary();
        ProfileMixerSwitch.IsOn = _profile.ConfigureMixer;
        UseDefaultSystemCaptureSwitch.IsOn = _profile.UseDefaultSystemCapture;
        RoutingModeBox.ItemsSource = Enum.GetValues<ProfileRoutingMode>();
        RoutingModeBox.SelectedItem = _profile.RoutingMode;
        ProfileVoiceGainBox.Value = _profile.VoiceGain * 100.0;
        ProfileSystemGainBox.Value = _profile.SystemGain * 100.0;
        ProfileDuckingSwitch.IsOn = _profile.DuckingEnabled;
        ProfileDuckAmountBox.Value = _profile.DuckAmount * 100.0;
        ProfileDuckThresholdBox.Value = _profile.DuckThresholdDb;
        ProfileDuckAttackBox.Value = _profile.DuckAttackMs;
        ProfileDuckHoldBox.Value = _profile.DuckHoldMs;
        ProfileDuckReleaseBox.Value = _profile.DuckReleaseMs;
        ProfileProcessFilterSwitch.IsOn = _profile.ProcessFilterEnabled;
        ProfileProcessFilterModeBox.ItemsSource = new[] { "Solo las marcadas", "Todas salvo las desmarcadas" };
        ProfileProcessFilterModeBox.SelectedIndex = _profile.ProcessFilterExclusionMode ? 1 : 0;
        ProfileApplicationRules.Clear();
        foreach (var rule in _profile.ApplicationMixRules)
            ProfileApplicationRules.Add(new ProfileApplicationRuleRow
            {
                Key = rule.Key,
                DisplayName = rule.DisplayName,
                GainPercent = rule.Gain * 100.0,
                Excluded = rule.Excluded
            });
        UpdateProfileRulesSummary();
        AdvancedRolesSwitch.IsOn = _profile.UseAdvancedRoles;
        AdvancedExpander.IsExpanded = _profile.UseAdvancedRoles
                                      || _profile.ConfigureWindowsInputDefaults
                                      || _profile.InputMode == ProfileInputMode.ProfileOverride
                                      || _profile.ConfigureInternalPipeline
                                      || _profile.ConfigureExternalEndpoints
                                      || _profile.ConfigurePluginChain
                                      || _profile.ConfigureMixer;
        UpdateInternalPipelineVisibility();
        UpdateInputVisibility();
        UpdateExternalMuteVisibility();
        UpdateProfileMixerVisibility();
        UpdateAdvancedVisibility();
        _ready = true;
    }

    private void BindIcons()
    {
        var items = Enum.GetValues<ProfileIconKind>().Select(kind => new IconChoice
        {
            Kind = kind,
            Glyph = ProfileIcons.Glyph(kind),
            Label = ProfileIcons.DisplayName(kind)
        }).ToList();
        IconGrid.ItemsSource = items;
        IconGrid.SelectedItem = items.FirstOrDefault(i => i.Kind == _profile.Icon) ?? items[0];
    }

    private void BindDevices()
    {
        var outputs = Controller.Audio.GetDevices(AudioFlow.Playback);
        var inputs = Controller.Audio.GetDevices(AudioFlow.Recording);
        BindBox(OutputBox, OutputWarning, outputs, _profile.Output, Loc.Get("ChooseOutput"));
        BindBox(InputBox, InputWarning, inputs, _profile.Input, Loc.Get("ChooseInput"));
        BindBox(OutputConsoleBox, null, outputs, RoleOrPrimary(_profile.OutputConsole, _profile.Output), Loc.Get("ChooseOutput"));
        BindBox(OutputMultimediaBox, null, outputs, RoleOrPrimary(_profile.OutputMultimedia, _profile.Output), Loc.Get("ChooseOutput"));
        BindBox(OutputCommunicationsBox, null, outputs, RoleOrPrimary(_profile.OutputCommunications, _profile.Output), Loc.Get("ChooseOutput"));
        var inputPrimary = _profile.InputMode == ProfileInputMode.ProfileOverride
                           && HasReference(_profile.InternalInput)
            ? _profile.InternalInput
            : _profile.Input;
        BindBox(InputConsoleBox, null, inputs, RoleOrPrimary(_profile.InputConsole, inputPrimary), Loc.Get("ChooseInput"));
        BindBox(InputMultimediaBox, null, inputs, RoleOrPrimary(_profile.InputMultimedia, inputPrimary), Loc.Get("ChooseInput"));
        BindBox(InputCommunicationsBox, null, inputs, RoleOrPrimary(_profile.InputCommunications, inputPrimary), Loc.Get("ChooseInput"));
        var internalInput = _profile.InputMode == ProfileInputMode.ProfileOverride
            || _profile.InputMode == ProfileInputMode.LegacyPerProfile
            ? RoleOrPrimary(_profile.InternalInput, _profile.Input)
            : Controller.Settings.GlobalPhysicalInput;
        BindBox(InternalInputBox, null, inputs, internalInput, Loc.Get("ChooseInput"));

        var finalDecision = Controller.Audio.ResolveFinalVirtualRender(outputs, _profile.FinalVirtualRender);
        var finalVirtual = finalDecision.Selected is null
            ? _profile.FinalVirtualRender
            : new SavedDeviceReference
            {
                Id = finalDecision.Selected.Id,
                Name = finalDecision.Selected.Name
            };
        BindBox(FinalVirtualRenderBox, null, outputs, finalVirtual, Loc.Get("ChooseOutput"));
        BindBox(SystemCaptureBox, null, outputs, _profile.SystemCapture, Loc.Get("ChooseOutput"));
        BindExternalMuteDevices(inputs);
    }

    private void BindExternalMuteDevices(IReadOnlyList<AudioDeviceInfo> inputs)
    {
        var items = inputs.Select(device => new DeviceChoice
        {
            Id = device.Id,
            Name = device.Availability == DeviceAvailability.Available
                ? device.Name
                : $"{device.Name} ({Loc.Get("NotConnected")})",
            Available = device.Availability == DeviceAvailability.Available
        }).ToList();
        foreach (var saved in _profile.ExternalMuteDevices)
        {
            if (items.Any(item => string.Equals(item.Id, saved.Id, StringComparison.OrdinalIgnoreCase))) continue;
            items.Add(new DeviceChoice
            {
                Id = saved.Id,
                Name = $"{(string.IsNullOrWhiteSpace(saved.Name) ? saved.Id : saved.Name)} ({Loc.Get("NotConnected")})",
                Available = false
            });
        }
        ExternalMuteDevicesList.ItemsSource = items;
        foreach (var item in items.Where(item => _profile.ExternalMuteDevices.Any(saved =>
                     string.Equals(saved.Id, item.Id, StringComparison.OrdinalIgnoreCase))))
            ExternalMuteDevicesList.SelectedItems.Add(item);
    }

    private static SavedDeviceReference RoleOrPrimary(SavedDeviceReference? role, SavedDeviceReference primary) =>
        role is not null && !string.IsNullOrWhiteSpace(role.Id) ? role : primary;

    private static void BindBox(ComboBox box, TextBlock? warning, IReadOnlyList<AudioDeviceInfo> devices, SavedDeviceReference current, string placeholder)
    {
        var items = devices.Select(d => new DeviceChoice
        {
            Id = d.Id,
            Name = d.Availability == DeviceAvailability.Available ? d.Name : $"{d.Name} ({Loc.Get("NotConnected")})",
            Available = d.Availability == DeviceAvailability.Available
        }).ToList();

        if (!string.IsNullOrWhiteSpace(current.Id) && items.All(i => !string.Equals(i.Id, current.Id, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new DeviceChoice
            {
                Id = current.Id,
                Name = $"{(string.IsNullOrWhiteSpace(current.Name) ? current.Id : current.Name)} ({Loc.Get("NotConnected")})",
                Available = false
            });
        }

        box.ItemsSource = items;
        box.DisplayMemberPath = nameof(DeviceChoice.Name);
        box.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Id, current.Id, StringComparison.OrdinalIgnoreCase));
        box.PlaceholderText = placeholder;
        if (warning is not null)
        {
            warning.Visibility = (!string.IsNullOrWhiteSpace(current.Id) && items.FirstOrDefault(i => i.Id == current.Id)?.Available == false)
                ? Visibility.Visible
                : Visibility.Collapsed;
            warning.Text = Loc.Get("NotConnected");
        }
    }

    private void AdvancedRolesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        if (AdvancedRolesSwitch.IsOn)
        {
            SeedAdvancedFromSimple();
        }

        UpdateAdvancedVisibility();
    }

    private void InternalPipelineSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateInternalPipelineVisibility();
    }

    private void ProfileInputOverrideSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _profile.InputMode = ProfileInputOverrideSwitch.IsOn
            ? ProfileInputMode.ProfileOverride
            : ProfileInputMode.FollowGlobal;
        UpdateInputVisibility();
    }

    private void WindowsInputDefaultsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_profile.InputMode == ProfileInputMode.FollowGlobal)
        {
            _profile.ConfigureWindowsInputDefaults = false;
            WindowsInputDefaultsSwitch.IsOn = false;
        }
        else
        {
            _profile.ConfigureWindowsInputDefaults = WindowsInputDefaultsSwitch.IsOn;
        }
        UpdateInputVisibility();
        UpdateAdvancedVisibility();
    }

    private void UpdateInternalPipelineVisibility()
    {
        InternalPipelinePanel.Visibility = InternalPipelineSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ProfileInputOverrideSwitch.Visibility = InternalPipelineSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        UseDefaultInternalInputSwitch.Visibility = _profile.InputMode == ProfileInputMode.LegacyPerProfile
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateInputVisibility()
    {
        var canConfigureWindowsInput = _profile.InputMode != ProfileInputMode.FollowGlobal;
        if (!canConfigureWindowsInput)
        {
            _profile.ConfigureWindowsInputDefaults = false;
            WindowsInputDefaultsSwitch.IsOn = false;
        }

        WindowsInputDefaultsSwitch.Visibility = canConfigureWindowsInput
            ? Visibility.Visible
            : Visibility.Collapsed;
        WindowsInputDefaultsSwitch.IsEnabled = canConfigureWindowsInput;
        InputProfilePanel.Visibility = canConfigureWindowsInput && _profile.ConfigureWindowsInputDefaults
            ? Visibility.Visible
            : Visibility.Collapsed;
        GlobalInputSummaryPanel.Visibility = canConfigureWindowsInput && _profile.ConfigureWindowsInputDefaults
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyInputNotice.Visibility = _profile.InputMode == ProfileInputMode.LegacyPerProfile
            ? Visibility.Visible
            : Visibility.Collapsed;

        var global = Controller.Settings.GlobalPhysicalInput;
        GlobalInputSummary.Text = Controller.Settings.GlobalInputMode == GlobalInputMode.Unconfigured
            ? LiteralCatalog.Get("La entrada física global se configura en Entrada y plugins antes de activar el flujo interno.")
            : LiteralCatalog.Get("La entrada física global es {0}; los cambios allí afectan a todos los perfiles.")
                .Replace("{0}", string.IsNullOrWhiteSpace(global.Name) ? global.Id : global.Name, StringComparison.Ordinal);
    }

    private void ConfigureGlobalInputButton_Click(object sender, RoutedEventArgs e) =>
        App.Instance.MainAppWindow?.NavigateToTag("inputPlugins");

    private void ExternalMuteSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateExternalMuteVisibility();
    }

    private void ExternalMuteAllSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ExternalMuteDevicesList.IsEnabled = !ExternalMuteAllSwitch.IsOn;
    }

    private void UpdateExternalMuteVisibility()
    {
        ExternalMutePanel.Visibility = ExternalMuteSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ExternalMuteDevicesList.IsEnabled = !ExternalMuteAllSwitch.IsOn;
    }

    private async void CapturePluginChainButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Controller.Engine.IsConnected) await Controller.Engine.ConnectAsync();
            var captured = await Controller.Engine.CaptureProfileStateAsync();
            _profile.PluginChain = captured.Plugins.Select(plugin => new ProfilePluginPresetEntry
            {
                Id = plugin.Id,
                Bypassed = plugin.Bypassed,
                StateBase64 = plugin.StateBase64
            }).ToList();
            ProfilePluginChainSwitch.IsOn = true;
            ErrorText.Text = string.Empty;
            UpdateProfilePluginChainSummary();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void UpdateProfilePluginChainSummary()
    {
        var count = _profile.PluginChain.Count;
        ProfilePluginChainSummary.Text = count == 0
            ? LiteralCatalog.Get("Preset vacío: al activar el perfil se quitarán todos los plugins.")
            : string.Format(LiteralCatalog.Get("{0} elemento(s), con orden, bypass y estado binario del plugin."), count);
    }

    private void ProfileMixerSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateProfileMixerVisibility();
    }

    private void UpdateProfileMixerVisibility() =>
        ProfileMixerPanel.Visibility = ProfileMixerSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

    private async void ImportMixerRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Controller.Engine.IsConnected) await Controller.Engine.ConnectAsync();
            var snapshot = await Controller.Engine.GetSnapshotAsync();
            var configured = snapshot.ProcessRules;
            if (configured.Count == 0)
            {
                ErrorText.Text = LiteralCatalog.Get("El Mezclador no tiene aplicaciones seleccionadas para copiar.");
                return;
            }

            ProfileApplicationRules.Clear();
            foreach (var rule in configured)
                ProfileApplicationRules.Add(new ProfileApplicationRuleRow
                {
                    Key = rule.Key,
                    DisplayName = rule.DisplayName,
                    GainPercent = rule.Gain * 100.0,
                    Excluded = rule.Excluded
                });
            ProfileProcessFilterSwitch.IsOn = true;
            ProfileProcessFilterModeBox.SelectedIndex = snapshot.ProcessFilterExclusionMode ? 1 : 0;
            ErrorText.Text = string.Empty;
            UpdateProfileRulesSummary();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void RemoveProfileRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var existing = ProfileApplicationRules.FirstOrDefault(rule =>
            string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) ProfileApplicationRules.Remove(existing);
        UpdateProfileRulesSummary();
    }

    private void UpdateProfileRulesSummary() =>
        ProfileRulesSummary.Text = string.Format(LiteralCatalog.Get("{0} aplicación(es) guardada(s); máximo 12."), ProfileApplicationRules.Count);

    private void SeedAdvancedFromSimple()
    {
        CopySelection(OutputBox, OutputConsoleBox, OutputMultimediaBox, OutputCommunicationsBox);
        if (_profile.ConfigureWindowsInputDefaults)
            CopySelection(InputBox, InputConsoleBox, InputMultimediaBox, InputCommunicationsBox);
    }

    private static void CopySelection(ComboBox source, params ComboBox[] targets)
    {
        foreach (var target in targets)
        {
            if (target.SelectedItem is null)
            {
                target.SelectedItem = source.SelectedItem;
            }
        }
    }

    private void UpdateAdvancedVisibility()
    {
        AdvancedRolesPanel.Visibility = AdvancedRolesSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        AdvancedInputRolesPanel.Visibility = AdvancedRolesSwitch.IsOn
                                              && _profile.InputMode != ProfileInputMode.FollowGlobal
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void HotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = HotkeyFormatter.ResolvePressedKey(e.Key, e.OriginalKey);
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var windows = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!HotkeyFormatter.TryCreate(key, control, alt, shift, windows, out var binding, out var errorKey))
        {
            ErrorText.Text = errorKey is null ? string.Empty : Loc.Get(errorKey);
            return;
        }

        if (HotkeyFormatter.Conflicts(binding, Controller.Profiles, _profile.Id))
        {
            ErrorText.Text = Loc.Get("ShortcutInUse");
            return;
        }

        if (!Controller.Hotkeys.TryRegister(binding, out var registerError))
        {
            ErrorText.Text = Loc.Get(registerError ?? "ShortcutInUse");
            return;
        }

        _profile.Hotkey = binding;
        HotkeyBox.Text = HotkeyFormatter.ToDisplay(binding);
        ErrorText.Text = string.Empty;
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _profile.Hotkey = new HotkeyBinding();
        HotkeyBox.Text = Loc.Get("NoShortcut");
        ErrorText.Text = string.Empty;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _profile.Name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_profile.Name))
        {
            ErrorText.Text = Loc.Get("MissingName");
            return;
        }

        if (IconGrid.SelectedItem is IconChoice icon)
        {
            _profile.Icon = icon.Kind;
        }

        if (OutputBox.SelectedItem is not DeviceChoice output)
        {
            ErrorText.Text = Loc.Get("MissingOutput");
            return;
        }

        _profile.Output = ToReference(output);

        var wasLegacyInput = _profile.InputMode == ProfileInputMode.LegacyPerProfile;
        if (_profile.InputMode != ProfileInputMode.LegacyPerProfile || _isNew)
        {
            _profile.InputMode = ProfileInputOverrideSwitch.IsOn
                ? ProfileInputMode.ProfileOverride
                : ProfileInputMode.FollowGlobal;
        }

        _profile.ConfigureWindowsInputDefaults = _profile.InputMode != ProfileInputMode.FollowGlobal
                                                  && WindowsInputDefaultsSwitch.IsOn;
        if (_profile.ConfigureWindowsInputDefaults)
        {
            if (InputBox.SelectedItem is not DeviceChoice input)
            {
                ErrorText.Text = Loc.Get("MissingInput");
                return;
            }

            _profile.Input = ToReference(input);
        }

        _profile.ConfigureInternalPipeline = InternalPipelineSwitch.IsOn;
        _profile.UseDefaultInternalInput = wasLegacyInput && UseDefaultInternalInputSwitch.IsOn;
        if (_profile.ConfigureInternalPipeline)
        {
            if (FinalVirtualRenderBox.SelectedItem is not DeviceChoice finalRender)
            {
                ErrorText.Text = LiteralCatalog.Get("Selecciona la entrada interna y el endpoint virtual final.");
                return;
            }
            if (_profile.InputMode is ProfileInputMode.ProfileOverride or ProfileInputMode.LegacyPerProfile)
            {
                if (InternalInputBox.SelectedItem is not DeviceChoice internalInput)
                {
                    ErrorText.Text = LiteralCatalog.Get("Selecciona la entrada interna y el endpoint virtual final.");
                    return;
                }

                if (_profile.InputMode == ProfileInputMode.ProfileOverride && !IsPhysicalCapture(internalInput))
                {
                    ErrorText.Text = Loc.Get("PhysicalInputRequired");
                    return;
                }

                _profile.InternalInput = ToReference(internalInput);
            }
            _profile.FinalVirtualRender = ToReference(finalRender);
            _profile.EngineBufferSize = double.IsNaN(EngineBufferBox.Value) ? 0 : (int)EngineBufferBox.Value;
            _profile.InternalMute = InternalMuteBox.SelectedItem is InternalMuteState mute
                ? mute
                : InternalMuteState.Preserve;
        }
        _profile.ConfigurePluginChain = ProfilePluginChainSwitch.IsOn;
        _profile.ConfigureExternalEndpoints = ExternalMuteSwitch.IsOn;
        if (_profile.ConfigureExternalEndpoints)
        {
            _profile.ExternalMuteAllRecordingDevices = ExternalMuteAllSwitch.IsOn;
            _profile.ExternalMuteDevices = ExternalMuteDevicesList.SelectedItems
                .OfType<DeviceChoice>()
                .Select(ToReference)
                .ToList();
            if (!_profile.ExternalMuteAllRecordingDevices && _profile.ExternalMuteDevices.Count == 0)
            {
                ErrorText.Text = LiteralCatalog.Get("Selecciona al menos un endpoint externo o activa la opción para todos.");
                return;
            }
            _profile.ForceExternalMuteState = ForceExternalMuteSwitch.IsOn;
            _profile.ExternalVolumeLockEnabled = ExternalVolumeLockSwitch.IsOn;
            _profile.ExternalVolumeScalar = (float)(SafeNumber(ExternalVolumeBox, 100) / 100.0);
        }
        _profile.ConfigureMixer = ProfileMixerSwitch.IsOn;
        if (_profile.ConfigureMixer)
        {
            _profile.UseDefaultSystemCapture = UseDefaultSystemCaptureSwitch.IsOn;
            _profile.RoutingMode = RoutingModeBox.SelectedItem is ProfileRoutingMode routingMode
                ? routingMode
                : ProfileRoutingMode.Voice;
            if (_profile.RoutingMode != ProfileRoutingMode.Voice
                && SystemCaptureBox.SelectedItem is not DeviceChoice)
            {
                ErrorText.Text = LiteralCatalog.Get("Selecciona el endpoint de audio del sistema.");
                return;
            }
            _profile.SystemCapture = SystemCaptureBox.SelectedItem is DeviceChoice systemCapture
                ? ToReference(systemCapture)
                : new SavedDeviceReference();
            _profile.VoiceGain = (float)(SafeNumber(ProfileVoiceGainBox, 100) / 100.0);
            _profile.SystemGain = (float)(SafeNumber(ProfileSystemGainBox, 100) / 100.0);
            _profile.DuckingEnabled = ProfileDuckingSwitch.IsOn;
            _profile.DuckAmount = (float)(SafeNumber(ProfileDuckAmountBox, 55) / 100.0);
            _profile.DuckThresholdDb = (float)SafeNumber(ProfileDuckThresholdBox, -36);
            _profile.DuckAttackMs = (int)SafeNumber(ProfileDuckAttackBox, 20);
            _profile.DuckHoldMs = (int)SafeNumber(ProfileDuckHoldBox, 200);
            _profile.DuckReleaseMs = (int)SafeNumber(ProfileDuckReleaseBox, 280);
            _profile.ProcessFilterEnabled = ProfileProcessFilterSwitch.IsOn;
            _profile.ProcessFilterExclusionMode = ProfileProcessFilterModeBox.SelectedIndex == 1;
            _profile.ApplicationMixRules = ProfileApplicationRules.Select(rule => new ProfileApplicationMixRule
            {
                Key = rule.Key,
                DisplayName = rule.DisplayName,
                Gain = (float)Math.Clamp(rule.GainPercent / 100.0, 0.0, 4.0),
                Excluded = rule.Excluded
            }).ToList();
        }
        _profile.UseAdvancedRoles = AdvancedRolesSwitch.IsOn;
        if (_profile.UseAdvancedRoles)
        {
            if (!TryReadRole(OutputConsoleBox, out var outputConsole) ||
                !TryReadRole(OutputMultimediaBox, out var outputMultimedia) ||
                !TryReadRole(OutputCommunicationsBox, out var outputCommunications))
            {
                ErrorText.Text = Loc.Get("MissingAdvancedDevices");
                return;
            }

            _profile.OutputConsole = outputConsole;
            _profile.OutputMultimedia = outputMultimedia;
            _profile.OutputCommunications = outputCommunications;
            if (_profile.InputMode != ProfileInputMode.FollowGlobal)
            {
                if (!TryReadRole(InputConsoleBox, out var inputConsole) ||
                    !TryReadRole(InputMultimediaBox, out var inputMultimedia) ||
                    !TryReadRole(InputCommunicationsBox, out var inputCommunications))
                {
                    ErrorText.Text = Loc.Get("MissingAdvancedDevices");
                    return;
                }

                _profile.InputConsole = inputConsole;
                _profile.InputMultimedia = inputMultimedia;
                _profile.InputCommunications = inputCommunications;
            }
        }
        else
        {
            _profile.OutputConsole = null;
            _profile.OutputMultimedia = null;
            _profile.OutputCommunications = null;
            _profile.InputConsole = null;
            _profile.InputMultimedia = null;
            _profile.InputCommunications = null;
        }

        Controller.AddOrUpdateProfile(_profile);
        App.Instance.MainAppWindow?.NavigateToProfiles();
    }

    private bool IsPhysicalCapture(DeviceChoice choice)
    {
        var physical = Controller.Audio.GetPhysicalCaptureDevices();
        return physical.Any(device =>
            (!string.IsNullOrWhiteSpace(choice.Id)
             && string.Equals(device.Id, choice.Id, StringComparison.OrdinalIgnoreCase))
            || (string.IsNullOrWhiteSpace(choice.Id)
                && string.Equals(device.Name, StripUnavailable(choice.Name), StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasReference(SavedDeviceReference? reference) =>
        reference is not null
        && (!string.IsNullOrWhiteSpace(reference.Id) || !string.IsNullOrWhiteSpace(reference.Name));

    private static bool TryReadRole(ComboBox box, out SavedDeviceReference reference)
    {
        if (box.SelectedItem is DeviceChoice choice)
        {
            reference = ToReference(choice);
            return true;
        }

        reference = new SavedDeviceReference();
        return false;
    }

    private static SavedDeviceReference ToReference(DeviceChoice choice) => new()
    {
        Id = choice.Id,
        Name = StripUnavailable(choice.Name)
    };

    private static double SafeNumber(NumberBox box, double fallback) => double.IsNaN(box.Value) ? fallback : box.Value;

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.MainAppWindow?.NavigateToProfiles();
    }

    private static string StripUnavailable(string name)
    {
        var marker = $" ({Loc.Get("NotConnected")})";
        return name.EndsWith(marker, StringComparison.Ordinal) ? name[..^marker.Length] : name;
    }

    private sealed class IconChoice
    {
        public ProfileIconKind Kind { get; init; }
        public string Glyph { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
    }

    private sealed class DeviceChoice
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool Available { get; init; }
    }
}
