using Microsoft.Extensions.Options;

namespace OpenConnect.LiveUI;

public sealed class LiveUIOptionsValidator : IValidateOptions<LiveUIOptions>
{
    public ValidateOptionsResult Validate(string? name, LiveUIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (!Enum.IsDefined(options.Discovery.Mode)) failures.Add("Discovery.Mode is unsupported.");
        if (options.Discovery.MaximumTargetsPerRoute is < 1 or > 10_000)
            failures.Add("Discovery.MaximumTargetsPerRoute must be between 1 and 10000.");
        if (options.Interaction.DefaultActionTimeout <= TimeSpan.Zero || options.Interaction.DefaultActionTimeout > TimeSpan.FromMinutes(2))
            failures.Add("Interaction.DefaultActionTimeout must be greater than zero and at most two minutes.");
        if (options.Security.MaximumActionIdLength is < 16 or > 256)
            failures.Add("Security.MaximumActionIdLength must be between 16 and 256.");
        if (options.Narration.SynchronizeWithSpeech && !options.Narration.Enabled)
            failures.Add("Narration.SynchronizeWithSpeech requires Narration.Enabled.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
