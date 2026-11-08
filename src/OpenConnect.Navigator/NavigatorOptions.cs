namespace OpenConnect.Navigator;

public sealed class NavigatorOptions
{
    public bool Enabled { get; set; } = true;
    public NavigatorMode Mode { get; set; } = NavigatorMode.Interactive;
    public NavigatorBrowserOptions Browser { get; set; } = new();
    public NavigatorOnboardingOptions Onboarding { get; set; } = new();
    public NavigatorCaptureOptions Capture { get; set; } = new();
    public NavigatorSpeechOptions Speech { get; set; } = new();
    public NavigatorLimitsOptions Limits { get; set; } = new();
}

public sealed class NavigatorBrowserOptions
{
    public NavigatorBrowserProvider Provider { get; set; } = NavigatorBrowserProvider.Disabled;
    public Uri? WorkerEndpoint { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public int MaxConcurrentBrowsers { get; set; } = 1;
}
public sealed class NavigatorOnboardingOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowDynamicTours { get; set; }
    public NavigatorHighlightProvider Highlighter { get; set; } = NavigatorHighlightProvider.Native;
    public int MaxSteps { get; set; } = 25;
}
public sealed class NavigatorCaptureOptions
{
    public bool Enabled { get; set; }
    public NavigatorCaptureMode[] AllowedModes { get; set; } = [NavigatorCaptureMode.Semantic];
    public TimeSpan ArtifactTtl { get; set; } = TimeSpan.FromHours(1);
}
public sealed class NavigatorSpeechOptions
{
    public bool Enabled { get; set; }
    public NavigatorSpeechProvider PreferredProvider { get; set; } = NavigatorSpeechProvider.Kokoro;
    public NavigatorSpeechFallback Fallback { get; set; } = NavigatorSpeechFallback.TextOnly;
    public string DefaultVoice { get; set; } = "af_heart";
    public string DefaultLanguage { get; set; } = "en-US";
    public int MaxCharacters { get; set; } = 1500;
    public SpeechModelProvisioning ModelProvisioning { get; set; } = SpeechModelProvisioning.Disabled;
    public SpeechCacheFailurePolicy CacheFailurePolicy { get; set; } = SpeechCacheFailurePolicy.FailWorker;
}
public sealed class NavigatorLimitsOptions
{
    public int MaxObservationNodes { get; set; } = 500;
    public int MaxObservationBytes { get; set; } = 65536;
    public int MaxPlanSteps { get; set; } = 25;
    public int MaxConcurrentMutationsPerSession { get; set; } = 1;
}
