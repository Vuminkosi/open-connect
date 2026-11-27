using Android.App;
using Android.Content.PM;
using Android.OS;

namespace OpenConnect.Samples.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        EnsureNotificationChannels();
    }

    private void EnsureNotificationChannels()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return;
        }

        var manager = GetSystemService(NotificationService) as NotificationManager;
        if (manager is null)
        {
            return;
        }

        var messagesChannel = new NotificationChannel(
            "oc_imsg_messages",
            "OpenConnect Messages",
            NotificationImportance.High)
        {
            Description = "Opaque wake-up alerts for encrypted OpenConnect messages."
        };
        var callsChannel = new NotificationChannel(
            "oc_imsg_calls",
            "OpenConnect Incoming Calls",
            NotificationImportance.High)
        {
            Description = "Incoming voice call invitations requiring explicit user consent."
        };

        manager.CreateNotificationChannel(messagesChannel);
        manager.CreateNotificationChannel(callsChannel);
    }
}
