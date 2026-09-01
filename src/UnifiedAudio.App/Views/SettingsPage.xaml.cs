using System.Diagnostics;
using UnifiedAudio.Helpers;
using UnifiedAudio.Models;
using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;

namespace UnifiedAudio.Views;

public sealed partial class SettingsPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private readonly UpdateService _updates;
    private bool _ready;
    private bool _updating;

    public SettingsPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        _updates = new UpdateService(Controller.Log);
        TitleText.Text = Loc.Get("Settings");
        ThemeLabel.Text = Loc.Get("Theme");
        AutomationProperties.SetName(ThemeBox, Loc.Get("Theme"));
        LanguageLabel.Text = Loc.Get("Language");
        AutomationProperties.SetName(LanguageBox, Loc.Get("Language"));
        LanguageRestartText.Text = Loc.Get("LanguageRestart");
        StartupSwitch.Header = Loc.Get("StartWithWindows");
        BackgroundSwitch.Header = Loc.Get("KeepBackground");
        RestoreDefaultsSwitch.Header = Loc.Get("RestoreDefaultsOnExit");
        LaunchMinimizedSwitch.Header = Loc.Get("LaunchMinimized");
        NotificationSwitch.Header = Loc.Get("ShowNotifications");
        AdvancedExpander.Header = Loc.Get("Advanced");
        AdvancedHelp.Text = Loc.Get("SettingsAdvancedHelp");
        AdvancedLoggingSwitch.Header = Loc.Get("WriteDetailedLogs");
        AdvancedRolesHint.Text = Loc.Get("SettingsAdvancedRolesHint");
        AdvancedRolesSwitch.Header = Loc.Get("UseAdvancedRoles");
        OutputConsoleLabel.Text = Loc.Format("OutputRole", Loc.Get("RoleDefault"));
        OutputMultimediaLabel.Text = Loc.Format("OutputRole", Loc.Get("RoleMedia"));
        OutputCommunicationsLabel.Text = Loc.Format("OutputRole", Loc.Get("RoleCalls"));
        InputConsoleLabel.Text = Loc.Format("InputRole", Loc.Get("RoleDefault"));
        InputMultimediaLabel.Text = Loc.Format("InputRole", Loc.Get("RoleMedia"));
        InputCommunicationsLabel.Text = Loc.Format("InputRole", Loc.Get("RoleCalls"));
        OpenAdvancedProfileButton.Content = Loc.Get("OpenProfileAdvanced");
        FactoryResetTitle.Text = Loc.Get("FactoryReset");
        FactoryResetHelp.Text = Loc.Get("FactoryResetHelp");
        FactoryResetButton.Content = Loc.Get("FactoryReset");
        AboutTitle.Text = Loc.Get("About");
        UpdateButton.Content = Loc.Get("CheckForUpdates");
        LogsButton.Content = Loc.Get("OpenLogs");
        UpdateStatus.Text = string.Empty;
        ThemeBox.ItemsSource = new[]
        {
            new ThemeChoice(AppTheme.System, Loc.Get("ThemeSystem")),
            new ThemeChoice(AppTheme.Light, Loc.Get("ThemeLight")),
            new ThemeChoice(AppTheme.Dark, Loc.Get("ThemeDark"))
        };
        ThemeBox.DisplayMemberPath = nameof(ThemeChoice.Label);
        LanguageBox.ItemsSource = new[]
        {
            new LanguageChoice(AppLanguage.System, Loc.Get("LanguageSystem")),
            new LanguageChoice(AppLanguage.English, Loc.Get("LanguageEnglish")),
            new LanguageChoice(AppLanguage.Spanish, Loc.Get("LanguageSpanish")),
            new LanguageChoice(AppLanguage.SimplifiedChinese, Loc.Get("LanguageChinese"))
        };
        LanguageBox.DisplayMemberPath = nameof(LanguageChoice.Label);
        AdvancedProfileBox.DisplayMemberPath = nameof(ProfileChoice.Label);
        ImportSourceBox.DisplayMemberPath = nameof(LegacyImportSource.DisplayName);
        DefaultProfileBox.DisplayMemberPath = nameof(ProfileChoice.Label);
        DefaultProfileBox.Header = Loc.Get("DefaultProfile");
        StartupGateBox.ItemsSource = Enum.GetValues<StartupGateMode>();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ready = false;
        ThemeBox.SelectedItem = ((ThemeChoice[])ThemeBox.ItemsSource!).First(t => t.Value == Controller.Settings.Theme);
        LanguageBox.SelectedItem = ((LanguageChoice[])LanguageBox.ItemsSource!).First(choice => choice.Value == Controller.Settings.Language);
        StartupSwitch.IsOn = Controller.Settings.StartWithWindows;
        BackgroundSwitch.IsOn = Controller.Settings.KeepRunningInBackground;
        RestoreDefaultsSwitch.IsOn = Controller.Settings.RestoreWindowsDefaultsOnExit;
        LaunchMinimizedSwitch.IsOn = Controller.Settings.LaunchMinimized;
        NotificationSwitch.IsOn = Controller.Settings.ShowNotifications;
        StartupGateBox.SelectedItem = Controller.Settings.StartupGate;
        StartupDelayBox.Value = Controller.Settings.StartupDelaySeconds;
        StartupProcessBox.Text = Controller.Settings.StartupWaitProcess;
        StartupTimeoutBox.Value = Controller.Settings.StartupWaitTimeoutSeconds;
        UpdateStartupPolicyVisibility();
        AdvancedLoggingSwitch.IsOn = Controller.Settings.WriteDetailedLogs;
        BindAdvancedProfiles();
        BindDefaultProfiles();
        BindAdvancedRoles();
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.4";
        BindAboutText(version);
        BindImports();
        _ready = true;
    }

    private void BindImports()
    {
        var selectedPath = (ImportSourceBox.SelectedItem as LegacyImportSource)?.Path;
        var sources = Controller.Importer.Discover(Controller.State);
        ImportSourceBox.ItemsSource = sources;
        ImportSourceBox.SelectedItem = sources.FirstOrDefault(source =>
            string.Equals(source.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
            ?? sources.FirstOrDefault(source => !source.AlreadyImported)
            ?? sources.FirstOrDefault();
        ImportSourceBox.PlaceholderText = sources.Count == 0
            ? LiteralCatalog.Get("No se detectaron configuraciones compatibles")
            : LiteralCatalog.Get("Selecciona una configuración");
    }

    private void RefreshImportsButton_Click(object sender, RoutedEventArgs e) => BindImports();

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImportSourceBox.SelectedItem is not LegacyImportSource source) return;
        try
        {
            var preview = Controller.Importer.Preview(source);
            var details = preview.Summary;
            if (preview.Warnings.Count > 0)
                details += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, preview.Warnings.Select(warning => "• " + warning));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = string.Format(LiteralCatalog.Get("Importar {0}"), source.Label),
                Content = details,
                PrimaryButtonText = LiteralCatalog.Get("Importar"),
                CloseButtonText = Loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await Controller.ImportLegacyAsync(source);
            ImportStatus.Title = LiteralCatalog.Get("Importación completada");
            ImportStatus.Message = result.Summary
                + (result.Warnings.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, result.Warnings));
            ImportStatus.Severity = InfoBarSeverity.Success;
            ImportStatus.IsOpen = true;
            BindAdvancedProfiles();
            BindImports();
        }
        catch (Exception ex)
        {
            ImportStatus.Title = LiteralCatalog.Get("No se pudo importar");
            ImportStatus.Message = ex.Message;
            ImportStatus.Severity = InfoBarSeverity.Error;
            ImportStatus.IsOpen = true;
        }
    }

    private void BindAdvancedProfiles()
    {
        var items = Controller.Profiles
            .Select(profile => new ProfileChoice(profile.Id, profile.Name))
            .ToList();
        AdvancedProfileBox.ItemsSource = items;
        AdvancedProfileBox.PlaceholderText = Loc.Get("ChooseProfileForAdvanced");
        var hasProfiles = items.Count > 0;
        AdvancedProfileBox.IsEnabled = hasProfiles;
        AdvancedRolesSwitch.IsEnabled = hasProfiles;
        OpenAdvancedProfileButton.IsEnabled = hasProfiles;
        if (!hasProfiles)
        {
            AdvancedProfileBox.SelectedItem = null;
            return;
        }

        var lastId = Controller.Settings.LastActivatedProfileId;
        AdvancedProfileBox.SelectedItem = items.FirstOrDefault(item => item.Id == lastId) ?? items[0];
    }

    private AudioProfile? SelectedProfile()
    {
        if (AdvancedProfileBox.SelectedItem is not ProfileChoice choice)
        {
            return null;
        }

        return Controller.Profiles.FirstOrDefault(profile => profile.Id == choice.Id);
    }

    private void BindAdvancedRoles()
    {
        var profile = SelectedProfile();
        var hasProfile = profile is not null;
        AdvancedRolesSwitch.IsEnabled = hasProfile;
        OpenAdvancedProfileButton.IsEnabled = hasProfile;
        if (profile is null)
        {
            AdvancedRolesSwitch.IsOn = false;
            AdvancedRolesPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AdvancedRolesSwitch.IsOn = profile.UseAdvancedRoles;
        AdvancedRolesPanel.Visibility = profile.UseAdvancedRoles ? Visibility.Visible : Visibility.Collapsed;
        AdvancedInputRolesPanel.Visibility = profile.UseAdvancedRoles
                                             && profile.InputMode != ProfileInputMode.FollowGlobal
            ? Visibility.Visible
            : Visibility.Collapsed;
        var outputs = Controller.Audio.GetDevices(AudioFlow.Playback);
        var inputs = Controller.Audio.GetDevices(AudioFlow.Recording);
        var inputPrimary = profile.InputMode == ProfileInputMode.ProfileOverride
                           && (!string.IsNullOrWhiteSpace(profile.InternalInput.Id)
                               || !string.IsNullOrWhiteSpace(profile.InternalInput.Name))
            ? profile.InternalInput
            : profile.Input;
        BindRoleBox(OutputConsoleBox, outputs, RoleOrPrimary(profile.OutputConsole, profile.Output), Loc.Get("ChooseOutput"));
        BindRoleBox(OutputMultimediaBox, outputs, RoleOrPrimary(profile.OutputMultimedia, profile.Output), Loc.Get("ChooseOutput"));
        BindRoleBox(OutputCommunicationsBox, outputs, RoleOrPrimary(profile.OutputCommunications, profile.Output), Loc.Get("ChooseOutput"));
        BindRoleBox(InputConsoleBox, inputs, RoleOrPrimary(profile.InputConsole, inputPrimary), Loc.Get("ChooseInput"));
        BindRoleBox(InputMultimediaBox, inputs, RoleOrPrimary(profile.InputMultimedia, inputPrimary), Loc.Get("ChooseInput"));
        BindRoleBox(InputCommunicationsBox, inputs, RoleOrPrimary(profile.InputCommunications, inputPrimary), Loc.Get("ChooseInput"));
    }

    private static SavedDeviceReference RoleOrPrimary(SavedDeviceReference? role, SavedDeviceReference primary) =>
        role is not null && !string.IsNullOrWhiteSpace(role.Id) ? role : primary;

    private static void BindRoleBox(ComboBox box, IReadOnlyList<AudioDeviceInfo> devices, SavedDeviceReference current, string placeholder)
    {
        var items = devices.Select(device => new DeviceChoice
        {
            Id = device.Id,
            Name = device.Availability == DeviceAvailability.Available ? device.Name : $"{device.Name} ({Loc.Get("NotConnected")})",
            DisplayName = device.Name
        }).ToList();

        if (!string.IsNullOrWhiteSpace(current.Id) && items.All(item => !string.Equals(item.Id, current.Id, StringComparison.OrdinalIgnoreCase)))
        {
            var name = string.IsNullOrWhiteSpace(current.Name) ? current.Id : current.Name;
            items.Insert(0, new DeviceChoice
            {
                Id = current.Id,
                Name = $"{name} ({Loc.Get("NotConnected")})",
                DisplayName = name
            });
        }

        box.ItemsSource = items;
        box.DisplayMemberPath = nameof(DeviceChoice.Name);
        box.PlaceholderText = placeholder;
        box.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Id, current.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void PersistSelectedAdvancedRoles()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            return;
        }

        profile.UseAdvancedRoles = AdvancedRolesSwitch.IsOn;
        if (!profile.UseAdvancedRoles)
        {
            profile.OutputConsole = null;
            profile.OutputMultimedia = null;
            profile.OutputCommunications = null;
            profile.InputConsole = null;
            profile.InputMultimedia = null;
            profile.InputCommunications = null;
        }
        else
        {
            profile.OutputConsole = ToReference(OutputConsoleBox, profile.Output);
            profile.OutputMultimedia = ToReference(OutputMultimediaBox, profile.Output);
            profile.OutputCommunications = ToReference(OutputCommunicationsBox, profile.Output);
            if (profile.InputMode != ProfileInputMode.FollowGlobal)
            {
                profile.InputConsole = ToReference(InputConsoleBox, profile.Input);
                profile.InputMultimedia = ToReference(InputMultimediaBox, profile.Input);
                profile.InputCommunications = ToReference(InputCommunicationsBox, profile.Input);
            }
        }

        Controller.AddOrUpdateProfile(profile);
    }

    private static SavedDeviceReference ToReference(ComboBox box, SavedDeviceReference fallback)
    {
        if (box.SelectedItem is DeviceChoice choice)
        {
            return new SavedDeviceReference
            {
                Id = choice.Id,
                Name = choice.DisplayName
            };
        }

        return new SavedDeviceReference { Id = fallback.Id, Name = fallback.Name };
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ThemeBox.SelectedItem is not ThemeChoice choice)
        {
            return;
        }

        Controller.Settings.Theme = choice.Value;
        Controller.Persist();
        App.Instance.MainAppWindow?.ApplyTheme();
    }

    private void BindDefaultProfiles()
    {
        var items = Controller.Profiles.Select(profile => new ProfileChoice(profile.Id, profile.Name)).ToList();
        DefaultProfileBox.ItemsSource = items;
        DefaultProfileBox.IsEnabled = items.Count > 0;
        DefaultProfileBox.PlaceholderText = Loc.Get("ChooseDefaultProfile");
        DefaultProfileBox.SelectedItem = items.FirstOrDefault(item => item.Id == Controller.Settings.DefaultProfileId)
                                         ?? items.FirstOrDefault();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedItem is not LanguageChoice choice) return;
        Controller.Settings.Language = choice.Value;
        Loc.Language = choice.Value;
        Controller.Persist();
        LanguageLabel.Text = Loc.Get("Language");
        LanguageRestartText.Text = Loc.Get("LanguageRestart");
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Controller.Settings.StartWithWindows = StartupSwitch.IsOn;
        await Controller.Startup.ApplyAsync(Controller.Settings);
        Controller.Persist();
    }

    private void BackgroundSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Controller.Settings.KeepRunningInBackground = BackgroundSwitch.IsOn;
        Controller.Persist();
    }

    private void LaunchMinimizedSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Controller.Settings.LaunchMinimized = LaunchMinimizedSwitch.IsOn;
        Controller.Persist();
    }

    private void NotificationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Controller.Settings.ShowNotifications = NotificationSwitch.IsOn;
        Controller.Persist();
    }

    private void RestoreDefaultsSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Controller.Settings.RestoreWindowsDefaultsOnExit = RestoreDefaultsSwitch.IsOn;
        Controller.Persist();
    }

    private void DefaultProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || DefaultProfileBox.SelectedItem is not ProfileChoice choice) return;
        Controller.Settings.DefaultProfileId = choice.Id;
        Controller.Persist();
    }

    private void StartupGateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || StartupGateBox.SelectedItem is not StartupGateMode mode) return;
        Controller.Settings.StartupGate = mode;
        UpdateStartupPolicyVisibility();
        Controller.Persist();
    }

    private void StartupPolicy_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;
        Controller.Settings.StartupDelaySeconds = double.IsNaN(StartupDelayBox.Value)
            ? 0
            : (int)Math.Clamp(StartupDelayBox.Value, 0, 300);
        Controller.Settings.StartupWaitTimeoutSeconds = double.IsNaN(StartupTimeoutBox.Value)
            ? 180
            : (int)Math.Clamp(StartupTimeoutBox.Value, 1, 180);
        Controller.Persist();
    }

    private void StartupProcessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        Controller.Settings.StartupWaitProcess = StartupProcessBox.Text.Trim();
        Controller.Persist();
    }

    private void UpdateStartupPolicyVisibility()
    {
        var mode = StartupGateBox.SelectedItem is StartupGateMode selected
            ? selected
            : StartupGateMode.None;
        StartupDelayBox.Visibility = mode == StartupGateMode.Delay ? Visibility.Visible : Visibility.Collapsed;
        StartupProcessPanel.Visibility = mode == StartupGateMode.WaitForProcess ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AdvancedLoggingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        Controller.Settings.WriteDetailedLogs = AdvancedLoggingSwitch.IsOn;
        Controller.Log.Verbose = AdvancedLoggingSwitch.IsOn;
        Controller.Persist();
    }

    private void AdvancedProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var previous = _ready;
        _ready = false;
        BindAdvancedRoles();
        _ready = previous;
    }

    private void AdvancedRolesSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        AdvancedRolesPanel.Visibility = AdvancedRolesSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        PersistSelectedAdvancedRoles();
    }

    private void AdvancedRoleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !AdvancedRolesSwitch.IsOn)
        {
            return;
        }

        PersistSelectedAdvancedRoles();
    }

    private void OpenAdvancedProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedProfileBox.SelectedItem is not ProfileChoice choice)
        {
            App.Instance.MainAppWindow?.NavigateToEditor(null);
            return;
        }

        App.Instance.MainAppWindow?.NavigateToEditor(choice.Id);
    }

    private void LogsButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Controller.Log.DirectoryPath,
            UseShellExecute = true
        });
    }

    private async void FactoryResetButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("FactoryResetConfirmTitle"),
            Content = Loc.Get("FactoryResetConfirmBody"),
            PrimaryButtonText = Loc.Get("FactoryResetConfirmButton"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        FactoryResetButton.IsEnabled = false;
        try
        {
            var result = await Controller.FactoryResetAsync();
            var complete = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("FactoryResetCompleteTitle"),
                Content = Loc.Format("FactoryResetCompleteBody", result.BackupDirectory),
                CloseButtonText = Loc.Get("Close")
            };
            await complete.ShowAsync();
            App.Instance.ExitApplication(persist: false);
        }
        catch (Exception ex)
        {
            Controller.Log.Error("Factory reset failed; original state was restored when possible.", ex);
            var failed = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("FactoryResetFailedTitle"),
                Content = Loc.Get("FactoryResetFailedBody"),
                CloseButtonText = Loc.Get("Close")
            };
            await failed.ShowAsync();
            App.Instance.ExitApplication();
        }
    }

    private void BindAboutText(string version)
    {
        var authorLink = new Hyperlink();
        authorLink.Inlines.Add(new Run { Text = AppIdentity.Author });
        authorLink.Click += AboutAuthorLink_Click;

        AboutBody.Inlines.Clear();
        AboutBody.Inlines.Add(new Run { Text = Loc.Format("AboutBodyPrefix", version) });
        AboutBody.Inlines.Add(authorLink);
        AboutBody.Inlines.Add(new Run { Text = Loc.Get("AboutBodySuffix") });
    }

    private void AboutAuthorLink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        OpenUrl(AppIdentity.RepositoryUrl);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
        UpdateStatus.Text = Loc.Get("UpdateChecking");

        try
        {
            var check = await _updates.CheckForUpdateAsync();
            if (!check.Succeeded)
            {
                UpdateStatus.Text = UpdateErrorText(check.ErrorCode);
                return;
            }

            if (!check.IsUpdateAvailable)
            {
                UpdateStatus.Text = Loc.Format("UpdateUpToDate", check.CurrentVersion.ToString(3));
                return;
            }

            var latest = check.LatestVersion?.ToString(3) ?? check.TagName ?? "new";
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Get("UpdateAvailableTitle"),
                Content = Loc.Format("UpdateAvailableBody", check.CurrentVersion.ToString(3), latest),
                PrimaryButtonText = Loc.Get("InstallUpdate"),
                CloseButtonText = Loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                UpdateStatus.Text = Loc.Get("UpdateCancelled");
                return;
            }

            UpdateStatus.Text = Loc.Get("UpdateDownloading");
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 0;
            var progress = new Progress<double>(value =>
            {
                UpdateProgress.Value = value * 100;
                UpdateStatus.Text = Loc.Format("UpdateDownloadingPercent", (int)Math.Round(value * 100));
            });
            var installer = await _updates.DownloadInstallerAsync(check, progress);
            UpdateStatus.Text = Loc.Get("UpdateInstalling");
            UpdateProgress.IsIndeterminate = true;
            _updates.LaunchInstaller(installer);
            await Task.Delay(600);
            App.Instance.ExitApplication();
        }
        catch (Exception ex)
        {
            Controller.Log.Error("In-app update failed.", ex);
            UpdateStatus.Text = Loc.Get("UpdateFailed");
        }
        finally
        {
            _updating = false;
            UpdateButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateProgress.IsIndeterminate = true;
        }
    }

    private static string UpdateErrorText(string? code) => code switch
    {
        "missing-installer" => Loc.Get("UpdateMissingInstaller"),
        "missing-hash" => Loc.Get("UpdateMissingHash"),
        "missing-release" or "invalid-version" => Loc.Get("UpdateInvalidRelease"),
        "github" => Loc.Get("UpdateGithubError"),
        _ => Loc.Get("UpdateFailed")
    };

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private sealed record ThemeChoice(AppTheme Value, string Label);
    private sealed record LanguageChoice(AppLanguage Value, string Label);
    private sealed record ProfileChoice(string Id, string Label);

    private sealed class DeviceChoice
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
    }
}
