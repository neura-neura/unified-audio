using UnifiedAudio.Helpers;
using UnifiedAudio.Interop;
using UnifiedAudio.Models;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace UnifiedAudio.Services;

public sealed class NotificationService
{
    private readonly AppLog _log;
    private bool _registered;

    public NotificationService(AppLog log)
    {
        _log = log;
    }

    public void Initialize()
    {
        try
        {
            AppIdentity.PrepareNotificationIdentity();
            if (!AppNotificationManager.IsSupported())
            {
                throw new InvalidOperationException("Windows App SDK notifications are not supported on this system.");
            }

            AppNotificationManager.Default.Register();
            _registered = true;
            AppIdentity.RestoreNotificationIdentity();
            _log.Info(AppIdentity.IsPackaged()
                ? "Registered packaged Windows App SDK notifications."
                : "Registered unpackaged Windows App SDK notifications.");
        }
        catch (Exception ex)
        {
            _registered = false;
            AppIdentity.RestoreNotificationIdentity();
            _log.Error("Windows App SDK notification registration failed.", ex);
        }
    }

    public void ShowActivation(ProfileActivationResult result)
    {
        var titleKey = result.Outcome switch
        {
            ActivationOutcomeKind.Success => "ActivatedTitle",
            ActivationOutcomeKind.Partial => "PartialTitle",
            _ => "FailedTitle"
        };
        Show(Loc.Format(titleKey, result.Profile.Name), result.Summary);
    }

    public void Show(string title, string body)
    {
        if (_registered)
        {
            try
            {
                AppIdentity.RestoreNotificationIdentity();
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(body)
                    .BuildNotification();
                notification.ExpiresOnReboot = true;
                AppNotificationManager.Default.Show(notification);
                _log.Info($"Windows App SDK notification shown: {title}");
                return;
            }
            catch (Exception ex)
            {
                _log.Error("Windows App SDK notification show failed.", ex);
            }
        }

        try
        {
            NativeToast.Show(title, body);
            _log.Warn($"Native Windows toast used after App SDK failure: {title}");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to show a Windows notification.", ex);
        }
    }

    public void Shutdown()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to unregister Windows notifications.", ex);
        }
    }
}
