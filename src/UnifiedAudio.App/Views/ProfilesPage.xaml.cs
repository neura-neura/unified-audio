using UnifiedAudio.Helpers;
using UnifiedAudio.Models;
using UnifiedAudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace UnifiedAudio.Views;

public sealed partial class ProfilesPage : Page
{
    private AppController Controller => App.Instance.Controller;
    private long _ignoreItemClickUntil;

    public ProfilesPage()
    {
        InitializeComponent();
        UiLocalizer.Apply(this);
        TitleText.Text = Loc.Get("AppName");
        RefreshButton.Content = Loc.Get("Refresh");
        AddButton.Content = Loc.Get("AddProfile");
        EmptyTitle.Text = Loc.Get("CreateFirstTitle");
        EmptyBody.Text = Loc.Get("CreateFirstBody");
        EmptyCreateButton.Content = Loc.Get("CreateProfile");
        AutomationProperties.SetName(RefreshButton, Loc.Get("Refresh"));
        AutomationProperties.SetName(AddButton, Loc.Get("AddProfile"));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Controller.StateChanged += OnStateChanged;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Controller.StateChanged -= OnStateChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        CurrentProfileText.Text = Loc.Format("CurrentProfile", Controller.CurrentProfileName());
        var activeId = Controller.DetectActiveProfileId();
        var cards = Controller.Profiles.Select(profile => CreateCard(profile, activeId)).ToList();
        ProfileList.ItemsSource = cards;
        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileList.Visibility = cards.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private ProfileCard CreateCard(AudioProfile profile, string activeId)
    {
        var outputName = Controller.Audio.GetDisplayName(AudioFlow.Playback, profile.Output);
        var outputOk = Controller.Audio.GetAvailability(AudioFlow.Playback, profile.Output) == DeviceAvailability.Available;
        var managesWindowsInput = profile.ConfigureWindowsInputDefaults;
        var inputName = managesWindowsInput
            ? Controller.Audio.GetDisplayName(AudioFlow.Recording, profile.Input)
            : Loc.Get("GlobalPhysicalInput");
        var inputOk = !managesWindowsInput
                       || Controller.Audio.GetAvailability(AudioFlow.Recording, profile.Input) == DeviceAvailability.Available;
        var missing = new List<string>();
        if (!outputOk)
        {
            missing.Add(Loc.Format("DeviceUnavailable", outputName));
        }

        if (!inputOk)
        {
            missing.Add(Loc.Format("DeviceUnavailable", inputName));
        }

        var isActive = profile.Id == activeId;
        var resources = Application.Current.Resources;
        return new ProfileCard
        {
            Id = profile.Id,
            Name = profile.Name,
            Glyph = ProfileIcons.Glyph(profile.Icon),
            DeviceSummary = managesWindowsInput
                ? Loc.Format("Plus", outputName, inputName)
                : Loc.Format("OutputLine", outputName),
            StatusText = missing.Count == 0
                ? (profile.Hotkey.Enabled ? HotkeyFormatter.ToDisplay(profile.Hotkey) : Loc.Get("NoShortcut"))
                : string.Join(" \u00B7 ", missing),
            ActiveLabel = Loc.Get("Active"),
            EditLabel = Loc.Get("Edit"),
            ActionLabel = missing.Count == 0 ? Loc.Get("Edit") : Loc.Get("Change"),
            DeleteLabel = Loc.Get("Delete"),
            CardBrush = isActive
                ? (Brush)resources["SubtleFillColorSecondaryBrush"]
                : (Brush)resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = isActive
                ? (Brush)resources["AccentFillColorDefaultBrush"]
                : (Brush)resources["CardStrokeColorDefaultBrush"],
            ActiveVisibility = isActive ? Visibility.Visible : Visibility.Collapsed
        };
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.MainAppWindow?.NavigateToEditor(null);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Controller.RefreshFromHardware();
        Refresh();
    }

    private async void ProfileList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (Environment.TickCount64 < _ignoreItemClickUntil)
        {
            return;
        }

        if (e.ClickedItem is ProfileCard card)
        {
            await Controller.ActivateProfileAsync(card.Id);
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoreItemClickUntil = Environment.TickCount64 + 400;
        if (sender is Button button && button.Tag is string id)
        {
            App.Instance.MainAppWindow?.NavigateToEditor(id);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _ignoreItemClickUntil = Environment.TickCount64 + 400;
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        var profile = Controller.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Get("DeleteTitle"),
            Content = Loc.Format("DeleteBody", profile.Name),
            PrimaryButtonText = Loc.Get("Delete"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Controller.DeleteProfile(id);
        }
    }
}
