using Microsoft.Extensions.Options;

namespace OpenConnect.Navigator;

public sealed class NavigatorOptionsValidator : IValidateOptions<NavigatorOptions>
{
    public ValidateOptionsResult Validate(string? name, NavigatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Browser.Provider) || !Enum.IsDefined(options.Onboarding.Highlighter) ||
            !Enum.IsDefined(options.Speech.PreferredProvider) || !Enum.IsDefined(options.Speech.Fallback) ||
            !Enum.IsDefined(options.Speech.ModelProvisioning) || !Enum.IsDefined(options.Speech.CacheFailurePolicy) ||
            options.Capture.AllowedModes.Any(x => !Enum.IsDefined(x))) errors.Add("Navigator options contain an undefined enum value.");
        if (options.Browser.MaxConcurrentBrowsers < 1 || options.Onboarding.MaxSteps is < 1 or > 25 || options.Limits.MaxPlanSteps is < 1 or > 25 ||
            options.Limits.MaxObservationNodes < 1 || options.Limits.MaxObservationBytes < 1 || options.Limits.MaxConcurrentMutationsPerSession < 1)
            errors.Add("Navigator limits must be positive; tour steps cannot exceed 25.");
        if (!options.Speech.Enabled && options.Speech.PreferredProvider != NavigatorSpeechProvider.Kokoro)
            errors.Add("A speech provider cannot be selected while speech is disabled.");
        if (options.Speech.Enabled && options.Speech.PreferredProvider == NavigatorSpeechProvider.Disabled)
            errors.Add("Enabled speech requires an explicit provider.");
        if (options.Browser.Provider == NavigatorBrowserProvider.Disabled && (options.Browser.WorkerEndpoint is not null || options.Browser.AllowedOrigins.Length > 0))
            errors.Add("A disabled browser adapter cannot configure a worker endpoint or allowed origins.");
        if (!options.Capture.Enabled && options.Capture.AllowedModes.Any(x => x != NavigatorCaptureMode.Semantic))
            errors.Add("Capture modes beyond semantic metadata cannot be configured while capture is disabled.");
        if (options.Capture.ArtifactTtl <= TimeSpan.Zero || options.Speech.MaxCharacters < 1) errors.Add("Capture TTL and speech character limit must be positive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
