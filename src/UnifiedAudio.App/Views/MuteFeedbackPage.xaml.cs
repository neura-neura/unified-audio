using UnifiedAudio.Helpers;
using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using UnifiedAudio.Models;
using Microsoft.UI;
using UnifiedAudio.Interop;
using Windows.Storage.Pickers;

namespace UnifiedAudio.Views;

public sealed partial class MuteFeedbackPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private bool _updating;
    private bool _settingsReady;

    public MuteFeedbackPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("MuteAndFeedback");
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Controller.MuteStateChanged += Controller_MuteStateChanged;
        Controller.ExternalMute.StateChanged += ExternalMute_StateChanged;
        Controller.Feedback.SettingsChanged += Feedback_SettingsChanged;
        Controller.Audio.DevicesChanged += Audio_DevicesChanged;
        _settingsReady = false;
        FeedbackSoundsSwitch.IsOn = Controller.Settings.PlayMuteFeedbackSounds;
        RefreshFeedbackOutputs();
        MuteSoundPathBox.Text = Controller.Settings.MuteSoundPath;
        UnmuteSoundPathBox.Text = Controller.Settings.UnmuteSoundPath;
        PttOnSoundPathBox.Text = Controller.Settings.PttOnSoundPath;
        PttOffSoundPathBox.Text = Controller.Settings.PttOffSoundPath;
        MuteSoundVolumeBox.Value = Controller.Settings.MuteSoundVolumePercent;
        UnmuteSoundVolumeBox.Value = Controller.Settings.UnmuteSoundVolumePercent;
        PttOnSoundVolumeBox.Value = Controller.Settings.PttOnSoundVolumePercent;
        PttOffSoundVolumeBox.Value = Controller.Settings.PttOffSoundVolumePercent;
        MuteHotkeyBox.Text = HotkeyFormatter.ToDisplay(Controller.Settings.MuteHotkey);
        MuteHotkeyModeBox.ItemsSource = Enum.GetValues<UnifiedAudio.Models.GlobalMuteHotkeyMode>();
        MuteHotkeyModeBox.SelectedItem = Controller.Settings.MuteHotkeyMode;
        ReleaseDelayBox.Value = Controller.Settings.MuteHotkeyReleaseDelayMilliseconds;
        HybridHoldBox.Value = Controller.Settings.HybridHoldMilliseconds;
        PassthroughSwitch.IsOn = Controller.Settings.MuteHotkeyPassthrough;
        WildcardSwitch.IsOn = Controller.Settings.MuteHotkey.Wildcard;
        var modifierSides = Enum.GetValues<HotkeyModifierSide>()
            .Select(side => new ModifierSideChoice(side, LiteralCatalog.Get(side.ToString())))
            .ToArray();
        ControlSideBox.ItemsSource = modifierSides;
        AltSideBox.ItemsSource = modifierSides;
        ShiftSideBox.ItemsSource = modifierSides;
        ControlSideBox.SelectedItem = modifierSides.First(choice => choice.Value == Controller.Settings.MuteHotkey.ControlSide);
        AltSideBox.SelectedItem = modifierSides.First(choice => choice.Value == Controller.Settings.MuteHotkey.AltSide);
        ShiftSideBox.SelectedItem = modifierSides.First(choice => choice.Value == Controller.Settings.MuteHotkey.ShiftSide);
        MuteOnlyHotkeyBox.Text = HotkeyFormatter.ToDisplay(Controller.Settings.MuteOnlyHotkey);
        UnmuteOnlyHotkeyBox.Text = HotkeyFormatter.ToDisplay(Controller.Settings.UnmuteOnlyHotkey);
        OverlayToggleHotkeyBox.Text = HotkeyFormatter.ToDisplay(Controller.Settings.OverlayToggleHotkey);
        OverlayLockHotkeyBox.Text = HotkeyFormatter.ToDisplay(Controller.Settings.OverlayLockHotkey);
        OsdSwitch.IsOn = Controller.Settings.ShowMuteOsd;
        ExcludeFullscreenOsdSwitch.IsOn = Controller.Settings.ExcludeFullscreenOsd;
        OsdDurationBox.Value = Controller.Settings.MuteOsdDurationMilliseconds;
        OsdPositionBox.ItemsSource = Enum.GetValues<MuteOsdPosition>();
        OsdPositionBox.SelectedItem = Controller.Settings.OsdPosition;
        RefreshOsdMonitors();
        MutedOsdTextBox.Text = IsBuiltInMutedText(Controller.Settings.MutedOsdText)
            ? LiteralCatalog.Get("Micrófono muteado") : Controller.Settings.MutedOsdText;
        UnmutedOsdTextBox.Text = IsBuiltInUnmutedText(Controller.Settings.UnmutedOsdText)
            ? LiteralCatalog.Get("Micrófono activo") : Controller.Settings.UnmutedOsdText;
        MutedOsdColorPicker.Color = UiColor.Parse(Controller.Settings.MutedOsdColor, Colors.DarkRed);
        UnmutedOsdColorPicker.Color = UiColor.Parse(Controller.Settings.UnmutedOsdColor, Colors.DodgerBlue);
        OverlayMutedColorPicker.Color = UiColor.Parse(Controller.Settings.OverlayMutedColor, Colors.DarkRed);
        OverlayIdleColorPicker.Color = UiColor.Parse(Controller.Settings.OverlayIdleColor, Colors.DimGray);
        OverlaySpeakingColorPicker.Color = UiColor.Parse(Controller.Settings.OverlaySpeakingColor, Colors.ForestGreen);
        OverlaySwitch.IsOn = Controller.Settings.ShowMuteOverlay;
        OverlayLockedSwitch.IsOn = Controller.Settings.OverlayLocked;
        OverlayVisibilityBox.ItemsSource = Enum.GetValues<OverlayVisibilityMode>();
        OverlayVisibilityBox.SelectedItem = Controller.Settings.OverlayVisibility;
        OverlayScaleBox.Value = Controller.Settings.OverlayScalePercent;
        OverlayOpacityBox.Value = Controller.Settings.OverlayOpacityPercent;
        OverlayXBox.Value = Controller.Settings.OverlayRelativeX * 100.0;
        OverlayYBox.Value = Controller.Settings.OverlayRelativeY * 100.0;
        ActivityThresholdBox.Value = Controller.Settings.OverlayActivityThresholdDb;
        _settingsReady = true;
        await RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _settingsReady = false;
        Controller.MuteStateChanged -= Controller_MuteStateChanged;
        Controller.ExternalMute.StateChanged -= ExternalMute_StateChanged;
        Controller.Feedback.SettingsChanged -= Feedback_SettingsChanged;
        Controller.Audio.DevicesChanged -= Audio_DevicesChanged;
    }

    private void Audio_DevicesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_settingsReady) return;
        _settingsReady = false;
        RefreshFeedbackOutputs();
        _settingsReady = true;
    });

    private void RefreshFeedbackOutputs()
    {
        var choices = new List<FeedbackOutputChoice>
        {
            new(string.Empty, string.Empty, LiteralCatalog.Get("Predeterminado de Windows"))
        };
        choices.AddRange(Controller.Audio.GetDevices(AudioFlow.Playback).Select(device =>
            new FeedbackOutputChoice(device.Id, device.Name,
                device.Availability == DeviceAvailability.Available
                    ? device.Name
                    : $"{device.Name} ({device.Availability})")));
        if (!string.IsNullOrWhiteSpace(Controller.Settings.FeedbackOutputDeviceId)
            && choices.All(choice => !string.Equals(choice.Id, Controller.Settings.FeedbackOutputDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new FeedbackOutputChoice(
                Controller.Settings.FeedbackOutputDeviceId,
                Controller.Settings.FeedbackOutputDeviceName,
                $"{Controller.Settings.FeedbackOutputDeviceName} (desconectado)"));
        }
        FeedbackOutputBox.ItemsSource = choices;
        FeedbackOutputBox.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.Id, Controller.Settings.FeedbackOutputDeviceId, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    private void RefreshOsdMonitors()
    {
        var choices = new List<OsdMonitorChoice>
        {
            new(string.Empty, LiteralCatalog.Get("Automático: monitor de la ventana activa"))
        };
        choices.AddRange(NativeMethods.EnumerateMonitors()
            .Select(monitor => new OsdMonitorChoice(monitor.Id, monitor.Label)));

        var selectedId = Controller.Settings.OsdMonitorId;
        if (!string.IsNullOrWhiteSpace(selectedId)
            && choices.All(choice => !string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new OsdMonitorChoice(selectedId, $"{selectedId} ({LiteralCatalog.Get("desconectado")})"));
        }

        OsdMonitorBox.ItemsSource = choices;
        OsdMonitorBox.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    private void Controller_MuteStateChanged(object? sender, EngineSnapshot snapshot) => Render(snapshot);

    private void ExternalMute_StateChanged(IReadOnlyList<ExternalEndpointStatus> endpoints) =>
        RenderExternalEndpoints(endpoints);

    private void Feedback_SettingsChanged(object? sender, EventArgs e)
    {
        _settingsReady = false;
        OverlaySwitch.IsOn = Controller.Settings.ShowMuteOverlay;
        OverlayLockedSwitch.IsOn = Controller.Settings.OverlayLocked;
        OverlayXBox.Value = Controller.Settings.OverlayRelativeX * 100.0;
        OverlayYBox.Value = Controller.Settings.OverlayRelativeY * 100.0;
        OverlayScaleBox.Value = Controller.Settings.OverlayScalePercent;
        OverlayOpacityBox.Value = Controller.Settings.OverlayOpacityPercent;
        RefreshOsdMonitors();
        _settingsReady = true;
    }

    private void FeedbackSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        Controller.Settings.PlayMuteFeedbackSounds = FeedbackSoundsSwitch.IsOn;
        Controller.Settings.ShowMuteOsd = OsdSwitch.IsOn;
        Controller.Settings.ExcludeFullscreenOsd = ExcludeFullscreenOsdSwitch.IsOn;
        Controller.Settings.ShowMuteOverlay = OverlaySwitch.IsOn;
        Controller.Settings.OverlayLocked = OverlayLockedSwitch.IsOn;
        PersistFeedbackSettings();
    }

    private void OsdDurationBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value)) return;
        Controller.Settings.MuteOsdDurationMilliseconds = (int)sender.Value;
        PersistFeedbackSettings();
    }

    private void SoundPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingsReady) return;
        Controller.Settings.MuteSoundPath = MuteSoundPathBox.Text.Trim();
        Controller.Settings.UnmuteSoundPath = UnmuteSoundPathBox.Text.Trim();
        Controller.Settings.PttOnSoundPath = PttOnSoundPathBox.Text.Trim();
        Controller.Settings.PttOffSoundPath = PttOffSoundPathBox.Text.Trim();
        PersistFeedbackSettings();
    }

    private async void ChooseSoundPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady || sender is not Button { Tag: string tag }) return;
        var window = App.Instance.MainAppWindow;
        if (window is null) return;

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".wav");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(window));
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            GetSoundPathBox(tag).Text = file.Path;
        }
        catch (Exception ex)
        {
            StatusBar.Title = LiteralCatalog.Get("No se pudo elegir el WAV");
            StatusBar.Message = ex.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private void ClearSoundPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady || sender is not Button { Tag: string tag }) return;
        GetSoundPathBox(tag).Text = string.Empty;
    }

    private TextBox GetSoundPathBox(string tag) => tag switch
    {
        "Mute" => MuteSoundPathBox,
        "Unmute" => UnmuteSoundPathBox,
        "PttOn" => PttOnSoundPathBox,
        "PttOff" => PttOffSoundPathBox,
        _ => throw new ArgumentOutOfRangeException(nameof(tag))
    };

    private void SoundVolumeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value) || double.IsInfinity(sender.Value)) return;
        var volume = (int)Math.Clamp(Math.Round(sender.Value), 0, 100);
        if (ReferenceEquals(sender, MuteSoundVolumeBox))
            Controller.Settings.MuteSoundVolumePercent = volume;
        else if (ReferenceEquals(sender, UnmuteSoundVolumeBox))
            Controller.Settings.UnmuteSoundVolumePercent = volume;
        else if (ReferenceEquals(sender, PttOnSoundVolumeBox))
            Controller.Settings.PttOnSoundVolumePercent = volume;
        else if (ReferenceEquals(sender, PttOffSoundVolumeBox))
            Controller.Settings.PttOffSoundVolumePercent = volume;
        else
            return;
        PersistFeedbackSettings();
    }

    private void FeedbackOutputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || FeedbackOutputBox.SelectedItem is not FeedbackOutputChoice choice) return;
        Controller.Settings.FeedbackOutputDeviceId = choice.Id;
        Controller.Settings.FeedbackOutputDeviceName = choice.Name;
        PersistFeedbackSettings();
    }

    private void ActivityThresholdBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value)) return;
        Controller.Settings.OverlayActivityThresholdDb = (float)sender.Value;
        PersistFeedbackSettings();
    }

    private void OverlayPlacementBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value)) return;
        Controller.Settings.OverlayScalePercent = (int)Math.Clamp(OverlayScaleBox.Value, 50, 300);
        Controller.Settings.OverlayRelativeX = Math.Clamp(OverlayXBox.Value / 100.0, 0.0, 1.0);
        Controller.Settings.OverlayRelativeY = Math.Clamp(OverlayYBox.Value / 100.0, 0.0, 1.0);
        PersistFeedbackSettings();
    }

    private void OverlayVisibilityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || OverlayVisibilityBox.SelectedItem is not OverlayVisibilityMode mode) return;
        Controller.Settings.OverlayVisibility = mode;
        PersistFeedbackSettings();
    }

    private void OverlayOpacityBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value) || double.IsInfinity(sender.Value)) return;
        Controller.Settings.OverlayOpacityPercent = (int)Math.Clamp(Math.Round(sender.Value), 10, 100);
        PersistFeedbackSettings();
    }

    private void OsdPositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || OsdPositionBox.SelectedItem is not MuteOsdPosition position) return;
        Controller.Settings.OsdPosition = position;
        PersistFeedbackSettings();
    }

    private void OsdMonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || OsdMonitorBox.SelectedItem is not OsdMonitorChoice monitor) return;
        Controller.Settings.OsdMonitorId = monitor.Id;
        PersistFeedbackSettings();
    }

    private void LocateOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        Controller.Feedback.ShowOverlayNow();
        StatusBar.Title = LiteralCatalog.Get("Overlay");
        StatusBar.Message = LiteralCatalog.Get("El overlay se mostró en el monitor configurado.");
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.IsOpen = true;
    }

    private void OsdTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingsReady) return;
        Controller.Settings.MutedOsdText = string.IsNullOrWhiteSpace(MutedOsdTextBox.Text) ? "Micrófono muteado" : MutedOsdTextBox.Text.Trim();
        Controller.Settings.UnmutedOsdText = string.IsNullOrWhiteSpace(UnmutedOsdTextBox.Text) ? "Micrófono activo" : UnmutedOsdTextBox.Text.Trim();
        PersistFeedbackSettings();
    }

    private void StateColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_settingsReady) return;
        Controller.Settings.MutedOsdColor = UiColor.ToHex(MutedOsdColorPicker.Color);
        Controller.Settings.UnmutedOsdColor = UiColor.ToHex(UnmutedOsdColorPicker.Color);
        Controller.Settings.OverlayMutedColor = UiColor.ToHex(OverlayMutedColorPicker.Color);
        Controller.Settings.OverlayIdleColor = UiColor.ToHex(OverlayIdleColorPicker.Color);
        Controller.Settings.OverlaySpeakingColor = UiColor.ToHex(OverlaySpeakingColorPicker.Color);
        PersistFeedbackSettings();
    }

    private void PersistFeedbackSettings()
    {
        Controller.Persist();
        Controller.Feedback.ApplySettings();
    }

    private void MuteHotkeyModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || MuteHotkeyModeBox.SelectedItem is not UnifiedAudio.Models.GlobalMuteHotkeyMode mode) return;
        Controller.Settings.MuteHotkeyMode = mode;
        Controller.Persist();
    }

    private void ReleaseDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value)) return;
        Controller.Settings.MuteHotkeyReleaseDelayMilliseconds = (int)sender.Value;
        Controller.Persist();
    }

    private void HybridHoldBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_settingsReady || double.IsNaN(sender.Value)) return;
        Controller.Settings.HybridHoldMilliseconds = (int)sender.Value;
        Controller.Persist();
    }

    private void PassthroughSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        Controller.Settings.MuteHotkeyPassthrough = PassthroughSwitch.IsOn;
        Controller.Hotkeys.MuteHotkeyPassthrough = PassthroughSwitch.IsOn;
        Controller.Persist();
    }

    private void MainMuteHotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = HotkeyFormatter.ResolvePressedKey(e.Key, e.OriginalKey);
        if (key == VirtualKey.None || HotkeyFormatter.IsModifier(key))
        {
            ShowHotkeyError("ShortcutInvalid");
            return;
        }
        var current = Controller.Settings.MuteHotkey;
        var candidate = new HotkeyBinding
        {
            Enabled = true,
            Control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
            Alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
            Shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
            Windows = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down),
            VirtualKey = (int)key,
            ScanCode = (int)e.KeyStatus.ScanCode,
            ExtendedKey = e.KeyStatus.IsExtendedKey,
            Wildcard = WildcardSwitch.IsOn,
            ControlSide = (ControlSideBox.SelectedItem as ModifierSideChoice)?.Value ?? current.ControlSide,
            AltSide = (AltSideBox.SelectedItem as ModifierSideChoice)?.Value ?? current.AltSide,
            ShiftSide = (ShiftSideBox.SelectedItem as ModifierSideChoice)?.Value ?? current.ShiftSide
        };
        TryApplyMainMuteHotkey(candidate);
    }

    private async void CaptureMouseButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureMouseButton.IsEnabled = false;
        CaptureMouseButton.Content = LiteralCatalog.Get("Pulsa un botón del mouse…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            TryApplyMainMuteHotkey(await Controller.Hotkeys.CaptureNextMainMouseButtonAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            ShowHotkeyError("MouseCaptureTimedOut");
        }
        finally
        {
            CaptureMouseButton.Content = LiteralCatalog.Get("Capturar siguiente botón del mouse");
            CaptureMouseButton.IsEnabled = true;
        }
    }

    private void ClearMainMuteHotkeyButton_Click(object sender, RoutedEventArgs e) =>
        TryApplyMainMuteHotkey(new HotkeyBinding());

    private void RestoreDefaultMainMuteHotkeyButton_Click(object sender, RoutedEventArgs e) =>
        TryApplyMainMuteHotkey(new HotkeyBinding
        {
            Enabled = true,
            Control = false,
            Alt = false,
            VirtualKey = 0x22,
            ScanCode = 0x51,
            ExtendedKey = false
        });

    private void MainHotkeyOption_Changed(object sender, object e)
    {
        if (!_settingsReady) return;
        var candidate = Controller.Settings.MuteHotkey.Clone();
        candidate.Wildcard = WildcardSwitch.IsOn;
        candidate.ControlSide = (ControlSideBox.SelectedItem as ModifierSideChoice)?.Value ?? HotkeyModifierSide.Neutral;
        candidate.AltSide = (AltSideBox.SelectedItem as ModifierSideChoice)?.Value ?? HotkeyModifierSide.Neutral;
        candidate.ShiftSide = (ShiftSideBox.SelectedItem as ModifierSideChoice)?.Value ?? HotkeyModifierSide.Neutral;
        TryApplyMainMuteHotkey(candidate);
    }

    private void TryApplyMainMuteHotkey(HotkeyBinding candidate)
    {
        string? errorKey = null;
        if (HotkeyFormatter.Conflicts(candidate, Controller.Profiles, null)
            || !Controller.Hotkeys.TrySetMainMuteBinding(candidate, out errorKey))
        {
            ShowHotkeyError(errorKey ?? "ShortcutInUse");
            return;
        }
        Controller.Settings.MuteHotkey = candidate;
        MuteHotkeyBox.Text = HotkeyFormatter.ToDisplay(candidate);
        Controller.Persist();
        StatusBar.IsOpen = false;
    }

    private void StateHotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = HotkeyFormatter.ResolvePressedKey(e.Key, e.OriginalKey);
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var windows = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!HotkeyFormatter.TryCreate(key, control, alt, shift, windows, out var candidate, out var errorKey))
        {
            ShowHotkeyError(errorKey);
            return;
        }
        if (HotkeyFormatter.Conflicts(candidate, Controller.Profiles, null))
        {
            ShowHotkeyError("ShortcutInUse");
            return;
        }

        var mute = ReferenceEquals(sender, MuteOnlyHotkeyBox)
            ? candidate
            : Controller.Settings.MuteOnlyHotkey.Clone();
        var unmute = ReferenceEquals(sender, UnmuteOnlyHotkeyBox)
            ? candidate
            : Controller.Settings.UnmuteOnlyHotkey.Clone();
        if (!Controller.Hotkeys.TryRegisterMuteBindings(mute, unmute, out errorKey))
        {
            ShowHotkeyError(errorKey);
            return;
        }

        Controller.Settings.MuteOnlyHotkey = mute;
        Controller.Settings.UnmuteOnlyHotkey = unmute;
        MuteOnlyHotkeyBox.Text = HotkeyFormatter.ToDisplay(mute);
        UnmuteOnlyHotkeyBox.Text = HotkeyFormatter.ToDisplay(unmute);
        Controller.Persist();
        StatusBar.IsOpen = false;
    }

    private void ClearStateHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var mute = sender is Button { Tag: string tag } && tag == "Mute"
            ? new HotkeyBinding()
            : Controller.Settings.MuteOnlyHotkey.Clone();
        var unmute = sender is Button { Tag: string otherTag } && otherTag == "Unmute"
            ? new HotkeyBinding()
            : Controller.Settings.UnmuteOnlyHotkey.Clone();
        if (!Controller.Hotkeys.TryRegisterMuteBindings(mute, unmute, out var errorKey))
        {
            ShowHotkeyError(errorKey);
            return;
        }
        Controller.Settings.MuteOnlyHotkey = mute;
        Controller.Settings.UnmuteOnlyHotkey = unmute;
        MuteOnlyHotkeyBox.Text = HotkeyFormatter.ToDisplay(mute);
        UnmuteOnlyHotkeyBox.Text = HotkeyFormatter.ToDisplay(unmute);
        Controller.Persist();
        StatusBar.IsOpen = false;
    }

    private void OverlayHotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = HotkeyFormatter.ResolvePressedKey(e.Key, e.OriginalKey);
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var windows = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!HotkeyFormatter.TryCreate(key, control, alt, shift, windows, out var candidate, out var errorKey)
            || HotkeyFormatter.Conflicts(candidate, Controller.Profiles, null))
        {
            ShowHotkeyError(errorKey ?? "ShortcutInUse");
            return;
        }

        var toggle = ReferenceEquals(sender, OverlayToggleHotkeyBox)
            ? candidate
            : Controller.Settings.OverlayToggleHotkey.Clone();
        var lockToggle = ReferenceEquals(sender, OverlayLockHotkeyBox)
            ? candidate
            : Controller.Settings.OverlayLockHotkey.Clone();
        if (!Controller.Hotkeys.TryRegisterOverlayBindings(toggle, lockToggle, out errorKey))
        {
            ShowHotkeyError(errorKey);
            return;
        }
        Controller.Settings.OverlayToggleHotkey = toggle;
        Controller.Settings.OverlayLockHotkey = lockToggle;
        OverlayToggleHotkeyBox.Text = HotkeyFormatter.ToDisplay(toggle);
        OverlayLockHotkeyBox.Text = HotkeyFormatter.ToDisplay(lockToggle);
        Controller.Persist();
        StatusBar.IsOpen = false;
    }

    private void ClearOverlayHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var toggle = sender is Button { Tag: string tag } && tag == "Toggle"
            ? new HotkeyBinding()
            : Controller.Settings.OverlayToggleHotkey.Clone();
        var lockToggle = sender is Button { Tag: string otherTag } && otherTag == "Lock"
            ? new HotkeyBinding()
            : Controller.Settings.OverlayLockHotkey.Clone();
        if (!Controller.Hotkeys.TryRegisterOverlayBindings(toggle, lockToggle, out var errorKey))
        {
            ShowHotkeyError(errorKey);
            return;
        }
        Controller.Settings.OverlayToggleHotkey = toggle;
        Controller.Settings.OverlayLockHotkey = lockToggle;
        OverlayToggleHotkeyBox.Text = HotkeyFormatter.ToDisplay(toggle);
        OverlayLockHotkeyBox.Text = HotkeyFormatter.ToDisplay(lockToggle);
        Controller.Persist();
        StatusBar.IsOpen = false;
    }

    private void ShowHotkeyError(string? errorKey)
    {
        StatusBar.Title = Loc.Get("ShortcutInUse");
        StatusBar.Message = Loc.Get(errorKey ?? "ShortcutInvalid");
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }

    private async Task RefreshAsync()
    {
        _updating = true;
        try
        {
            if (!Controller.Engine.IsConnected) await Controller.Engine.ConnectAsync();
            Render(await Controller.Engine.GetSnapshotAsync());
            RenderExternalEndpoints(Controller.ExternalMute.GetStatuses());
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _updating = false;
        }
    }

    private async void MuteSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        MuteSwitch.IsEnabled = false;
        try
        {
            await Controller.SetInternalMuteAsync(MuteSwitch.IsOn);
            await Task.Delay(100);
            Render(await Controller.Engine.GetSnapshotAsync());
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            MuteSwitch.IsEnabled = true;
            _updating = false;
        }
    }

    private void Render(EngineSnapshot snapshot)
    {
        MuteSwitch.IsOn = snapshot.MutedRequested;
        MuteStateText.Text = LiteralCatalog.Get(snapshot.MutedRequested ? "Micrófono muteado" : "Micrófono desmuteado");
        MuteDetailText.Text = snapshot.MuteSettled
            ? LiteralCatalog.Get("Gate asentado: salida de voz en cero digital exacto.")
            : LiteralCatalog.Get(snapshot.MutedRequested ? "Aplicando rampa corta antipop." : "La voz puede pasar al mezclador.");
    }

    private void RenderExternalEndpoints(IReadOnlyList<ExternalEndpointStatus> endpoints)
    {
        ExternalMuteStateText.Text = endpoints.Count == 0
            ? LiteralCatalog.Get("El perfil actual no controla endpoints externos.")
            : string.Join(Environment.NewLine, endpoints.Select(endpoint => endpoint.Available
                ? string.Format(LiteralCatalog.Get("{0}: {1}, volumen {2:F0}%"), endpoint.Name,
                    LiteralCatalog.Get(endpoint.Muted == true ? "muteado" : "activo"), endpoint.VolumeScalar.GetValueOrDefault() * 100)
                : string.Format(LiteralCatalog.Get("{0}: no disponible"), endpoint.Name)));
    }

    private void ShowError(Exception exception)
    {
        StatusBar.Title = Loc.Get("EngineUnavailable");
        StatusBar.Message = exception.Message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }

    private sealed record FeedbackOutputChoice(string Id, string Name, string Label);
    private sealed record OsdMonitorChoice(string Id, string Label);
    private sealed record ModifierSideChoice(HotkeyModifierSide Value, string Label);

    private static bool IsBuiltInMutedText(string text) => text is
        "Micrófono muteado" or "Microphone muted" or "麦克风已静音";

    private static bool IsBuiltInUnmutedText(string text) => text is
        "Micrófono activo" or "Microphone active" or "麦克风活动";
}
