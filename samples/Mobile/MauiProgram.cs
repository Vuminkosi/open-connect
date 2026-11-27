using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using OpenConnect.Client;
using OpenConnect.Client.Messaging;
using OpenConnect.Components.Blazor;
using OpenConnect.Samples.Mobile.Services.NativeCalling;
using OpenConnect.Samples.Mobile.Services.Notifications;
using OpenConnect.Samples.Mobile.Services.QrScanner;
using OpenConnect.Samples.Mobile.Services.SecureStorage;

namespace OpenConnect.Samples.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Host owns MudBlazor providers (per OpenConnect.Components.Blazor contract).
        builder.Services.AddMudServices();
        builder.Services.AddOpenConnectComponents();

        // Reuse existing OpenConnect MAUI audio capture & VAD pipeline.
        builder.Services.AddOpenConnectMauiAudio();

        // Register platform-backed profile-namespaced secure storage BEFORE AddOpenConnectMessagingClient
        // so the SDK's TryAddSingleton uses MAUI SecureStorage rather than in-memory storage.
        builder.Services.AddSingleton<IMessagingProfileStorage, MauiSecureProfileStorage>();
        builder.Services.AddSingleton<IEncryptedProfileCache, MauiEncryptedProfileCache>();
        builder.Services.AddOpenConnectMessagingClient();

        // Mobile platform adapters (no hardcoded company, gateway, or Firebase secrets).
        builder.Services.AddSingleton<IMobileQrScannerService, MobileQrScannerService>();
        builder.Services.AddSingleton<IMobileNotificationService, MobileNotificationService>();
        builder.Services.AddSingleton<IMobileCallingService, MobileCallingService>();

        return builder.Build();
    }
}
