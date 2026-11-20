using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

internal sealed class BrowserLiveUIActionConfirmation(IJSRuntime jsRuntime, IServiceProvider services) : ILiveUIActionConfirmation
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
        var mudPrompt = false;
        IJSObjectReference? module = null;
        var confirmationId = Guid.NewGuid().ToString("N");
        try
        {
            if (IsRoutineInteraction(context))
                return true;
            var message = $"Allow {context.Action.Type} on '{label}'?";
            if (value is not null)
                message += $"\nValue: {value}";
            if (state is not null)
                message += $"\nExpected state: {state} = {stateValue ?? "true"}";
            module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken,
                "./_content/OpenConnect.LiveUI.Blazor/liveui.js").ConfigureAwait(false);
            if (services.GetService<IDialogService>() is { } dialogs)
            {
                mudPrompt = true;
                prompt = ConfirmWithMudAsync(dialogs, module, context, confirmationId, cancellationToken);
            }
            else
                prompt = module.InvokeAsync<bool>("confirmAction", CancellationToken.None, message,
                    context.Action.Timeout.TotalMilliseconds, confirmationId).AsTask();
            return await prompt.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (mudPrompt && prompt is not null)
            {
                try { await prompt.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception) { }
            }
            else if (module is not null)
            {
                try
                { await module.InvokeVoidAsync("cancelConfirmation", confirmationId).ConfigureAwait(false); }
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
                try
                { await module.DisposeAsync().ConfigureAwait(false); }
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

    private static async Task<bool> ConfirmWithMudAsync(IDialogService dialogs, IJSObjectReference module,
        LiveUIExecutionContext context, string confirmationId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(context.Action.Timeout.TotalMilliseconds, 1, 120_000)));
        var active = false;
        IDialogReference? reference = null;
        try
        {
            active = await module.InvokeAsync<bool>("beginMudConfirmation", timeout.Token, confirmationId);
            if (!active) return false;
            var label = context.Target!.Label;
            var description = context.Action.Type switch
            {
                LiveUIActionType.SelectTab => $"Switch to the {label} view?",
                LiveUIActionType.SetValue => $"Change {label}?",
                LiveUIActionType.Select => $"Change the selection for {label}?",
                LiveUIActionType.Navigate => $"Open {label}?",
                LiveUIActionType.Open => $"Open {label}?",
                LiveUIActionType.Close => $"Close {label}?",
                LiveUIActionType.Click => $"Activate {label}?",
                LiveUIActionType.Focus => $"Focus on {label}?",
                LiveUIActionType.Scroll => $"Scroll to {label}?",
                LiveUIActionType.Highlight => $"Highlight {label}?",
                LiveUIActionType.ClearHighlight => $"Clear the highlight on {label}?",
                LiveUIActionType.Inspect => $"Inspect {label}?",
                LiveUIActionType.WaitForState => $"Wait for {label} to change?",
                _ => $"Use {label}?"
            };
            var parameters = new DialogParameters<LiveUIActionApprovalDialog>
            {
                { dialog => dialog.Description, description },
                { dialog => dialog.Value, context.Action.Parameters.Value },
                { dialog => dialog.StateName, context.Action.Parameters.StateName },
                { dialog => dialog.StateValue, context.Action.Parameters.StateValue }
            };
            var showing = dialogs.ShowAsync<LiveUIActionApprovalDialog>("Confirm action", parameters, new DialogOptions
            {
                Position = DialogPosition.Center,
                MaxWidth = MaxWidth.Small,
                FullWidth = true,
                BackdropClick = false,
                CloseOnEscapeKey = true
            });
            try { reference = await showing.WaitAsync(timeout.Token); }
            catch
            {
                _ = CloseLateDialogAsync(showing);
                throw;
            }
            if (!await module.InvokeAsync<bool>("prepareMudConfirmation", timeout.Token, confirmationId))
                return false;
            var result = await reference.Result.WaitAsync(timeout.Token);
            return result is { Canceled: false, Data: true };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        finally
        {
            reference?.Close(DialogResult.Cancel());
            try { await module.InvokeVoidAsync("endMudConfirmation", confirmationId); }
            catch (Exception) { }
        }
    }

    private static async Task CloseLateDialogAsync(Task<IDialogReference> showing)
    {
        try { (await showing).Close(DialogResult.Cancel()); }
        catch (Exception) { }
    }

    private static bool Displayable(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(IsUnsafeCharacter);

    private static bool DisplayableOptional(string? value, int maximum) =>
        value is null || value.Length <= maximum && !value.Any(IsUnsafeCharacter);

    private static bool IsUnsafeCharacter(char character) =>
        char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format;

    private static bool IsRoutineInteraction(LiveUIExecutionContext context)
    {
        if (context.Action.Type is LiveUIActionType.Focus or LiveUIActionType.Scroll or LiveUIActionType.Highlight or
            LiveUIActionType.ClearHighlight or LiveUIActionType.Inspect or LiveUIActionType.WaitForState)
            return true;
        var target = context.Target;
        if (target is null || LooksDestructive(target.Label))
            return false;
        return context.Action.Type switch
        {
            LiveUIActionType.SelectTab => target.Kind == LiveUITargetKind.Tab,
            LiveUIActionType.SetValue => target.Kind == LiveUITargetKind.Input &&
                !string.IsNullOrEmpty(context.Action.Parameters.Value),
            LiveUIActionType.Select => target.Kind == LiveUITargetKind.Select &&
                !string.IsNullOrEmpty(context.Action.Parameters.Value) && !LooksDestructive(context.Action.Parameters.Value),
            _ => false
        };
    }

    private static bool LooksDestructive(string text) =>
        new[] { "delete", "remove", "erase", "purge", "destroy", "discard", "wipe", "trash" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}
