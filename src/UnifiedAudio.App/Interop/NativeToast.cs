using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using UnifiedAudio.Services;

namespace UnifiedAudio.Interop;

/// <summary>
/// Fallback toast path for unpackaged builds if AppNotificationManager is unavailable.
/// Uses the same AUMID registered by <see cref="AppIdentity"/>.
/// </summary>
internal static class NativeToast
{
    public static void Show(string title, string body)
    {
        var xml = $"""
            <toast>
              <visual>
                <binding template="ToastGeneric">
                  <text>{Escape(title)}</text>
                  <text>{Escape(body)}</text>
                </binding>
              </visual>
            </toast>
            """;

        var document = new XmlDocument();
        document.LoadXml(xml);
        var toast = new ToastNotification(document);
        ToastNotificationManager.CreateToastNotifier(AppIdentity.AppUserModelId).Show(toast);
    }

    private static string Escape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;
}
