using UnifiedAudio.Helpers;
using UnifiedAudio.Models;
using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnifiedAudio.Views;

public sealed partial class AutomationPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private AudioProfile? _profile;
    private MuteActionDefinition? _action;
    private bool _loading;

    public AutomationPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("AutomationAndIntegrations");
        ActionTypeBox.ItemsSource = Enum.GetValues<MuteActionType>();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ProfileBox.ItemsSource = Controller.Profiles;
        ProfileBox.SelectedIndex = Controller.Profiles.Count > 0 ? 0 : -1;
        _loading = false;
        LoadProfile();
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) LoadProfile();
    }

    private void LoadProfile()
    {
        _profile = ProfileBox.SelectedItem is AudioProfile selected ? selected.Clone() : null;
        var enabled = _profile is not null;
        LinkedApplicationBox.IsEnabled = enabled;
        ForegroundOnlySwitch.IsEnabled = enabled;
        AfkTimeoutBox.IsEnabled = enabled;
        AutoExitAppsBox.IsEnabled = enabled;
        if (_profile is null)
        {
            ActionList.ItemsSource = null;
            return;
        }
        LinkedApplicationBox.Text = _profile.LinkedApplication;
        ForegroundOnlySwitch.IsOn = _profile.LinkedApplicationForegroundOnly;
        AfkTimeoutBox.Value = _profile.AfkTimeoutMilliseconds;
        AutoExitAppsBox.Text = string.Join(Environment.NewLine, _profile.AutoExitApplications);
        RefreshActions();
    }

    private void RefreshActions()
    {
        ActionList.ItemsSource = null;
        ActionList.ItemsSource = _profile?.MuteActions;
    }

    private void SaveRulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        _profile.LinkedApplication = LinkedApplicationBox.Text?.Trim() ?? string.Empty;
        _profile.LinkedApplicationForegroundOnly = ForegroundOnlySwitch.IsOn;
        _profile.AfkTimeoutMilliseconds = double.IsNaN(AfkTimeoutBox.Value) ? 0 : (int)AfkTimeoutBox.Value;
        _profile.AutoExitApplications = (AutoExitAppsBox.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Controller.AddOrUpdateProfile(_profile.Clone());
    }

    private void NewActionButton_Click(object sender, RoutedEventArgs e)
    {
        _action = new MuteActionDefinition { Name = LiteralCatalog.Get("Nueva acción"), RunWhenMuted = true, RunWhenUnmuted = true };
        LoadActionEditor();
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActionList.SelectedItem is MuteActionDefinition selected)
        {
            _action = selected.Clone();
            LoadActionEditor();
        }
    }

    private void LoadActionEditor()
    {
        if (_action is null) return;
        ActionNameBox.Text = _action.Name;
        ActionTypeBox.SelectedItem = _action.Type;
        ExecutableBox.Text = _action.FileName;
        ArgumentsBox.Text = _action.Arguments;
        PowerShellBox.Text = _action.PowerShellScript;
        VoicemeeterDllBox.Text = _action.FileName;
        VoicemeeterParameterBox.Text = _action.VoicemeeterParameter;
        VoicemeeterMutedValueBox.Value = _action.VoicemeeterMutedValue;
        VoicemeeterUnmutedValueBox.Value = _action.VoicemeeterUnmutedValue;
        ActionTimeoutBox.Value = _action.TimeoutSeconds;
        ActionEnabledSwitch.IsOn = _action.Enabled;
        ActionTrustedSwitch.IsOn = _action.Trusted;
        RunMutedSwitch.IsOn = _action.RunWhenMuted;
        RunUnmutedSwitch.IsOn = _action.RunWhenUnmuted;
        UpdateActionTypeVisibility();
    }

    private void ActionTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionTypeVisibility();

    private void UpdateActionTypeVisibility()
    {
        var powershell = ActionTypeBox.SelectedItem is MuteActionType.PowerShell;
        var voicemeeter = ActionTypeBox.SelectedItem is MuteActionType.Voicemeeter;
        PowerShellBox.Visibility = powershell ? Visibility.Visible : Visibility.Collapsed;
        ExecutableBox.Visibility = powershell || voicemeeter ? Visibility.Collapsed : Visibility.Visible;
        ArgumentsBox.Visibility = powershell || voicemeeter ? Visibility.Collapsed : Visibility.Visible;
        VoicemeeterPanel.Visibility = voicemeeter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null || _action is null) return;
        _action.Name = string.IsNullOrWhiteSpace(ActionNameBox.Text) ? LiteralCatalog.Get("Acción") : ActionNameBox.Text.Trim();
        _action.Type = ActionTypeBox.SelectedItem is MuteActionType type ? type : MuteActionType.Program;
        _action.FileName = ExecutableBox.Text?.Trim() ?? string.Empty;
        _action.Arguments = ArgumentsBox.Text ?? string.Empty;
        _action.PowerShellScript = PowerShellBox.Text ?? string.Empty;
        if (_action.Type == MuteActionType.Voicemeeter)
        {
            _action.FileName = VoicemeeterDllBox.Text?.Trim() ?? string.Empty;
            _action.VoicemeeterParameter = string.IsNullOrWhiteSpace(VoicemeeterParameterBox.Text)
                ? "Strip[0].Mute" : VoicemeeterParameterBox.Text.Trim();
            _action.VoicemeeterMutedValue = (float)(double.IsNaN(VoicemeeterMutedValueBox.Value) ? 1 : VoicemeeterMutedValueBox.Value);
            _action.VoicemeeterUnmutedValue = (float)(double.IsNaN(VoicemeeterUnmutedValueBox.Value) ? 0 : VoicemeeterUnmutedValueBox.Value);
        }
        _action.TimeoutSeconds = double.IsNaN(ActionTimeoutBox.Value) ? 10 : (int)ActionTimeoutBox.Value;
        _action.Enabled = ActionEnabledSwitch.IsOn;
        _action.Trusted = ActionTrustedSwitch.IsOn;
        _action.RunWhenMuted = RunMutedSwitch.IsOn;
        _action.RunWhenUnmuted = RunUnmutedSwitch.IsOn;

        var index = _profile.MuteActions.FindIndex(candidate => candidate.Id == _action.Id);
        if (index >= 0) _profile.MuteActions[index] = _action.Clone();
        else _profile.MuteActions.Add(_action.Clone());
        Controller.AddOrUpdateProfile(_profile.Clone());
        RefreshActions();
    }

    private void DeleteActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null || ActionList.SelectedItem is not MuteActionDefinition selected) return;
        _profile.MuteActions.RemoveAll(action => action.Id == selected.Id);
        Controller.AddOrUpdateProfile(_profile.Clone());
        _action = null;
        RefreshActions();
    }
}
