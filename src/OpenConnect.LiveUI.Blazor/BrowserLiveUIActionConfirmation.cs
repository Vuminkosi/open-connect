using Microsoft.JSInterop;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

internal sealed class BrowserLiveUIActionConfirmation(IJSRuntime jsRuntime) : ILiveUIActionConfirmation
{
    private int _active;

    public async ValueTask<bool> ConfirmAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var label = context.Target?.Label;
        var value = context.Action.Parameters.Value;
        var state = context.Action.Parameters.StateName;
        var stateValue = context.Action.Parameters.StateValue;
        if (!Displayable(label, 120) || !DisplayableOptional(value, 256) ||
            !DisplayableOptional(state, 32) || !DisplayableOptional(stateValue, 256) ||
            state is null && stateValue is not null)
            return false;
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return false;

        Task<bool>? prompt = null;
        IJSObjectReference? module = null;
        var confirmationId = Guid.NewGuid().ToString("N");
        try
        {
            var message = $"Allow {context.Action.Type} on '{label}'?";
            if (value is not null) message += $"\nValue: {value}";
            if (state is not null) message += $"\nExpected state: {state} = {stateValue ?? "true"}";
            module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken,
                "./_content/OpenConnect.LiveUI.Blazor/liveui.js").ConfigureAwait(false);
            prompt = module.InvokeAsync<bool>("confirmAction", CancellationToken.None, message,
                context.Action.Timeout.TotalMilliseconds, confirmationId).AsTask();
            return await prompt.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (module is not null)
            {
                try { await module.InvokeVoidAsync("cancelConfirmation", confirmationId).ConfigureAwait(false); }
                catch (Exception) { }
            }
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (module is not null)
            {
                try { await module.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            if (prompt is null || prompt.IsCompleted)
                Interlocked.Exchange(ref _active, 0);
            else
                _ = prompt.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    Interlocked.Exchange(ref _active, 0);
                },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static bool Displayable(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(IsUnsafeCharacter);

    private static bool DisplayableOptional(string? value, int maximum) =>
        value is null || value.Length <= maximum && !value.Any(IsUnsafeCharacter);

    private static bool IsUnsafeCharacter(char character) =>
        char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format;
}
