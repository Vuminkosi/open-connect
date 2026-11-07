namespace OpenConnect.Navigator.Blazor;

/// <summary>One tour-validation problem, in human language.</summary>
/// <param name="StepIndex">Zero-based step index, or -1 for tour-level problems.</param>
/// <param name="Message">Human-language description (no codes or stack traces).</param>
public sealed record NavigatorTourProblem(int StepIndex, string Message);

/// <summary>Result of <see cref="NavigatorTourService.ValidateTour"/>.</summary>
/// <param name="Ok">True when the tour may start.</param>
/// <param name="Problems">Human-language problems; empty when <c>Ok</c>.</param>
public sealed record NavigatorTourValidation(bool Ok, IReadOnlyList<NavigatorTourProblem> Problems);

/// <summary>Lifecycle status of the overlay tour.</summary>
public enum NavigatorTourStatus
{
    /// <summary>No tour is showing.</summary>
    Idle,
    /// <summary>A tour step is shown in the overlay.</summary>
    Active,
    /// <summary>The tour card is dismissed but progress is kept.</summary>
    Paused,
    /// <summary>The last step was completed.</summary>
    Finished,
    /// <summary>The tour was ended early.</summary>
    Skipped,
}

/// <summary>
/// Viewport rectangle for a registered target, in CSS pixels relative to
/// the viewport. Produced only as a progressive enhancement by host JS;
/// the service never fakes a position. Null means "no measured position"
/// (unknown target or no measurement yet) and callers must fall back to
/// the overlay card / text tour. Browser pixel positioning itself is
/// NOT VERIFIED by unit tests — see <c>GetTargetRect</c>.
/// </summary>
public sealed record NavigatorTargetRect(double X, double Y, double Width, double Height);

/// <summary>
/// Scoped tour state: target registry (register/unregister with route
/// scoping, mirroring the TS <c>TargetRegistry</c>) plus the overlay tour
/// state machine (start/next/back/go-to/pause/resume/stop).
/// DOM-free and SSR-safe: no JS interop, safe to construct during prerendering.
/// </summary>
public sealed class NavigatorTourService
{
    public const int TourMaxSteps = 25;
    public const int TourTitleMaxLength = 120;
    public const int TourBodyMaxLength = 1000;
    public const int TargetIdMaxLength = 128;
    public const int RouteIdMaxLength = 256;
    public const int TargetLabelMaxLength = 120;
    public const int TargetDescriptionMaxLength = 500;

    private readonly object _gate = new();

    // NAV-REM-BLZ-02: key by (routeId, targetId) so two routes may reuse the
    // same bare target id without colliding. Bare-id lookups are explicit
    // any-route fallbacks and never the disposal path for anchors.
    private readonly Dictionary<(string RouteId, string TargetId), NavigatorTarget> _targets = new();
    private readonly Dictionary<(string RouteId, string TargetId), NavigatorTargetRect> _rects = new();

    private List<NavigatorTourStep> _steps = [];
    private int _index;
    private NavigatorTourStatus _status = NavigatorTourStatus.Idle;
    private IReadOnlyList<NavigatorTourProblem> _lastProblems = Array.Empty<NavigatorTourProblem>();

    // Focus seam (DOM-free, testable): the overlay stores the element that
    // had focus before the tour started; the service requests restoration
    // when the tour ends. Host wiring assigns FocusRestore to real JS
    // interop; tests assign a lambda or inspect FocusRestoreLog.
    private string? _priorFocusHandle;
    private Action<string?>? _focusRestore;
    private readonly List<string?> _focusRestoreLog = new();

    /// <summary>Raised on the owning sync context after every state change.</summary>
    public event Action? Changed;

    public int Size { get { lock (_gate) return _targets.Count; } }

    public NavigatorTourStatus Status { get { lock (_gate) return _status; } }

    public int Index { get { lock (_gate) return _index; } }

    public IReadOnlyList<NavigatorTourStep> Steps { get { lock (_gate) return _steps.AsReadOnly(); } }

    public IReadOnlyList<NavigatorTourProblem> LastProblems { get { lock (_gate) return _lastProblems; } }

    /// <summary>Handle of the element focused before the tour started, if captured.</summary>
    public string? PriorFocusHandle { get { lock (_gate) return _priorFocusHandle; } }

    /// <summary>
    /// Best-effort focus-restoration hook. Assigned by the host (real focus
    /// JS) or by tests (recording lambda). Invoked with the stored handle
    /// (possibly null) when the tour finishes, is skipped/stopped, or is
    /// reset while in progress. Never throws: exceptions are swallowed.
    /// </summary>
    public Action<string?>? FocusRestore
    {
        get { lock (_gate) return _focusRestore; }
        set { lock (_gate) _focusRestore = value; }
    }

    /// <summary>
    /// Ordered log of focus-restoration attempts (handle per attempt).
    /// Test seam: assert restoration happened without a browser.
    /// </summary>
    public IReadOnlyList<string?> FocusRestoreLog
    {
        get { lock (_gate) return _focusRestoreLog.ToArray(); }
    }

    public NavigatorTourStep? CurrentStep
    {
        get
        {
            lock (_gate)
            {
                return _status is NavigatorTourStatus.Active or NavigatorTourStatus.Paused
                    && _index >= 0 && _index < _steps.Count
                    ? _steps[_index]
                    : null;
            }
        }
    }

    /// <summary>
    /// Register (or re-register) a target scoped to its route. Re-registering
    /// the same (routeId, targetId) keeps a single entry and bumps
    /// <c>Revision</c> so overlays can detect remounts. The same bare
    /// target id on a different route is a distinct entry with its own
    /// revision sequence starting at 1.
    /// </summary>
    public NavigatorTarget Register(string targetId, string routeId, string label, string? description = null)
    {
        CheckField(targetId, "Target name", TargetIdMaxLength);
        CheckField(routeId, "Screen name", RouteIdMaxLength);
        CheckField(label, "Label", TargetLabelMaxLength);
        if (description is not null && description.Length > TargetDescriptionMaxLength)
            throw new ArgumentException($"Description must be at most {TargetDescriptionMaxLength} characters.", nameof(description));

        lock (_gate)
        {
            var key = (routeId, targetId);
            var revision = _targets.TryGetValue(key, out var existing) ? existing.Revision + 1 : 1;
            var target = new NavigatorTarget(targetId, routeId, label, description, revision);
            _targets[key] = target;
            return target;
        }
    }

    /// <summary>
    /// Legacy bare-id removal: removes every route's entry with this target
    /// id. Prefer the scoped <c>Unregister(targetId, routeId)</c> overload so
    /// one route's disposal never removes another route's target.
    /// Returns true when at least one entry was present.
    /// </summary>
    public bool Unregister(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return false;
        lock (_gate)
        {
            var owned = _targets.Keys.Where(k => k.TargetId == targetId).ToArray();
            foreach (var key in owned)
            {
                _targets.Remove(key);
                _rects.Remove(key);
            }

            return owned.Length > 0;
        }
    }

    /// <summary>
    /// Scoped removal: removes only the (routeId, targetId) entry, leaving
    /// other routes' same-named targets untouched. Returns true when present.
    /// This is the disposal path anchors must use.
    /// </summary>
    public bool Unregister(string targetId, string routeId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return false;
        if (string.IsNullOrWhiteSpace(routeId)) return false;
        lock (_gate)
        {
            var key = (routeId, targetId);
            _rects.Remove(key);
            return _targets.Remove(key);
        }
    }

    /// <summary>
    /// Any-route lookup (legacy): returns the single matching target, or the
    /// first ordered by route when several routes share the id. Null when
    /// none match. Prefer the scoped overload when a route scope is known.
    /// </summary>
    public NavigatorTarget? Get(string targetId)
        => Get(targetId, routeId: null);

    /// <summary>
    /// Scoped lookup: when <paramref name="routeId"/> is provided, resolves
    /// only the (routeId, targetId) entry. When null/empty, falls back to the
    /// any-route rule above.
    /// </summary>
    public NavigatorTarget? Get(string targetId, string? routeId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(routeId))
                return _targets.TryGetValue((routeId, targetId), out var scoped) ? scoped : null;

            return _targets.Values
                .Where(t => t.TargetId == targetId)
                .OrderBy(t => t.RouteId, StringComparer.Ordinal)
                .ThenBy(t => t.TargetId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }

    /// <summary>List targets owned by one route, sorted by id.</summary>
    public IReadOnlyList<NavigatorTarget> ListForRoute(string routeId)
    {
        lock (_gate)
            return _targets.Values
                .Where(t => t.RouteId == routeId)
                .OrderBy(t => t.TargetId, StringComparer.Ordinal)
                .ToArray();
    }

    /// <summary>Remove every registered target and rect (for example on logout).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _targets.Clear();
            _rects.Clear();
        }
    }

    /// <summary>
    /// Remove every target owned by one route (provider unmount). Only that
    /// route's entries (and their rects) are removed; other routes are
    /// untouched.
    /// </summary>
    /// <returns>Number of entries removed.</returns>
    public int UnregisterRoute(string routeId)
    {
        lock (_gate)
        {
            var owned = _targets.Keys.Where(k => k.RouteId == routeId).ToArray();
            foreach (var key in owned)
            {
                _targets.Remove(key);
                _rects.Remove(key);
            }

            return owned.Length;
        }
    }

    /// <summary>
    /// Record a measured viewport rect for a registered target (progressive
    /// enhancement from host JS). Throws for unknown (routeId, targetId).
    /// </summary>
    public void SetTargetRect(string targetId, string routeId, NavigatorTargetRect rect)
    {
        ArgumentNullException.ThrowIfNull(rect);
        CheckField(targetId, "Target name", TargetIdMaxLength);
        CheckField(routeId, "Screen name", RouteIdMaxLength);
        lock (_gate)
        {
            if (!_targets.ContainsKey((routeId, targetId)))
                throw new ArgumentException($"Unknown target \"{targetId}\". Register it before measuring.", nameof(targetId));
            _rects[(routeId, targetId)] = rect;
        }
    }

    /// <summary>
    /// Bounding-rect contract for the highlight ring. Returns the measured
    /// rect, or null when the target is unknown, the route scope does not
    /// match, or no measurement was recorded. Callers must treat null as
    /// "position unknown" and fall back to the overlay card / text tour —
    /// never fake a position. Browser pixel positioning is NOT VERIFIED by
    /// unit tests; only this null-safe contract is.
    /// </summary>
    public NavigatorTargetRect? GetTargetRect(string targetId, string? routeId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(routeId))
                return _rects.TryGetValue((routeId, targetId), out var scoped) ? scoped : null;

            return _rects
                .Where(kv => kv.Key.TargetId == targetId)
                .OrderBy(kv => kv.Key.RouteId, StringComparer.Ordinal)
                .Select(kv => (NavigatorTargetRect?)kv.Value)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Validate tour steps: bounds (1–25 steps, title/body length), non-empty
    /// human text, and — for referenced targets — existence plus route scope.
    /// Route scoping resolves against (routeId, targetId): a step resolves
    /// only when that composite is registered. Pure: safe on server and
    /// client, with or without a DOM.
    /// </summary>
    public NavigatorTourValidation ValidateTour(IReadOnlyList<NavigatorTourStep> steps, string? routeId = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var problems = new List<NavigatorTourProblem>();
        if (steps.Count == 0)
        {
            problems.Add(new NavigatorTourProblem(-1, "The tour has no steps to show."));
            return new NavigatorTourValidation(false, problems);
        }

        if (steps.Count > TourMaxSteps)
            problems.Add(new NavigatorTourProblem(-1, $"The tour has too many steps (maximum {TourMaxSteps})."));

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (string.IsNullOrWhiteSpace(step.Title))
                problems.Add(new NavigatorTourProblem(i, "Step title must not be empty."));
            else if (step.Title.Length > TourTitleMaxLength)
                problems.Add(new NavigatorTourProblem(i, $"Step title must be at most {TourTitleMaxLength} characters."));

            if (string.IsNullOrWhiteSpace(step.Body))
                problems.Add(new NavigatorTourProblem(i, "Step body must not be empty."));
            else if (step.Body.Length > TourBodyMaxLength)
                problems.Add(new NavigatorTourProblem(i, $"Step body must be at most {TourBodyMaxLength} characters."));

            if (step.TargetId is not null)
            {
                var scope = step.RouteId ?? routeId;
                NavigatorTarget? target;
                if (scope is not null)
                {
                    target = Get(step.TargetId, scope);
                    if (target is null)
                    {
                        // Distinguish "never registered" from "belongs elsewhere".
                        var elsewhere = Get(step.TargetId);
                        problems.Add(elsewhere is null
                            ? new NavigatorTourProblem(i, $"Unknown target \"{step.TargetId}\". Register it before touring.")
                            : new NavigatorTourProblem(i, $"Target \"{step.TargetId}\" belongs to another screen and is not shown here."));
                    }
                }
                else
                {
                    target = Get(step.TargetId);
                    if (target is null)
                        problems.Add(new NavigatorTourProblem(i, $"Unknown target \"{step.TargetId}\". Register it before touring."));
                }
            }
        }

        return new NavigatorTourValidation(problems.Count == 0, problems);
    }

    /// <summary>
    /// Render the static text-only tour (fallback when no overlay is shown).
    /// Pure function: works on server and client, with or without a DOM.
    /// </summary>
    public static string RenderTextTour(IReadOnlyList<NavigatorTourStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) return "No tour steps available.";
        var lines = new List<string> { $"App tour — {steps.Count} {(steps.Count == 1 ? "step" : "steps")}" };
        for (var i = 0; i < steps.Count; i++)
        {
            lines.Add(string.Empty);
            lines.Add($"Step {i + 1} of {steps.Count}: {steps[i].Title}");
            lines.Add(steps[i].Body);
        }

        return string.Join("\n", lines);
    }

    /// <summary>Pure progress caption: "Step 2 of 5".</summary>
    public static string ProgressText(int stepIndex, int stepTotal) =>
        $"Step {stepIndex + 1} of {stepTotal}";

    /// <summary>Validate and start the overlay tour at the first step.</summary>
    /// <param name="steps">Tour steps (validated before any state change).</param>
    /// <param name="routeId">Active route scope for target resolution.</param>
    /// <param name="priorFocusHandle">
    /// Opaque handle of the element focused before the tour (for example an
    /// element id captured by the overlay). Stored and passed to
    /// <see cref="FocusRestore"/> when the tour finishes, is skipped/stopped,
    /// or is reset while in progress. Null means "no handle captured".
    /// </param>
    /// <returns>True when the tour started; false leaves state untouched.</returns>
    public bool StartTour(IReadOnlyList<NavigatorTourStep> steps, string? routeId = null, string? priorFocusHandle = null)
    {
        var validation = ValidateTour(steps, routeId);
        lock (_gate)
        {
            _lastProblems = validation.Problems;
            if (!validation.Ok) return false;
            _steps = [.. steps];
            _index = 0;
            _status = NavigatorTourStatus.Active;
            _priorFocusHandle = priorFocusHandle;
        }

        Notify();
        return true;
    }

    public void Next()
    {
        bool changed;
        bool finished = false;
        lock (_gate)
        {
            if (_status != NavigatorTourStatus.Active || _steps.Count == 0) return;
            if (_index + 1 >= _steps.Count)
            {
                _status = NavigatorTourStatus.Finished;
                finished = true;
            }
            else
            {
                _index++;
            }

            changed = true;
        }

        if (finished) TryRestoreFocus();
        if (changed) Notify();
    }

    public void Back()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status != NavigatorTourStatus.Active || _steps.Count == 0) return;
            var next = Math.Max(0, _index - 1);
            changed = next != _index;
            _index = next;
        }

        if (changed) Notify();
    }

    public void GoTo(int stepIndex)
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status is not (NavigatorTourStatus.Active or NavigatorTourStatus.Paused) || _steps.Count == 0) return;
            var clamped = Math.Min(Math.Max(0, stepIndex), _steps.Count - 1);
            changed = clamped != _index || _status == NavigatorTourStatus.Paused;
            _index = clamped;
            _status = NavigatorTourStatus.Active;
        }

        if (changed) Notify();
    }

    /// <summary>
    /// Pause the tour (dismiss the card but keep progress at the current
    /// step). Escape semantics: pressing Escape pauses — it never ends the
    /// tour and never discards progress. Call <see cref="Stop"/> to end the
    /// tour early (Skipped). Focus is NOT restored on pause; it is restored
    /// when the paused tour is finished, skipped/stopped, or reset.
    /// </summary>
    public void Pause()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status == NavigatorTourStatus.Active)
            {
                _status = NavigatorTourStatus.Paused;
                changed = true;
            }
        }

        if (changed) Notify();
    }

    public void Resume()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status == NavigatorTourStatus.Paused)
            {
                _status = NavigatorTourStatus.Active;
                changed = true;
            }
        }

        if (changed) Notify();
    }

    /// <summary>
    /// End the tour early (overlay closed, status Skipped). This is separate
    /// from <see cref="Pause"/>: Stop discards the session and requests
    /// best-effort focus restoration. Progress is discarded.
    /// </summary>
    public void Stop()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_status is NavigatorTourStatus.Active or NavigatorTourStatus.Paused)
            {
                _status = NavigatorTourStatus.Skipped;
                changed = true;
            }
        }

        if (changed)
        {
            TryRestoreFocus();
            Notify();
        }
    }

    /// <summary>
    /// Reset to idle (for example on logout/teardown). When a tour was in
    /// progress (Active/Paused), requests best-effort focus restoration.
    /// </summary>
    public void Reset()
    {
        bool changed;
        bool inProgress;
        lock (_gate)
        {
            inProgress = _status is NavigatorTourStatus.Active or NavigatorTourStatus.Paused;
            changed = _status != NavigatorTourStatus.Idle || _steps.Count != 0;
            _steps = [];
            _index = 0;
            _status = NavigatorTourStatus.Idle;
            _lastProblems = Array.Empty<NavigatorTourProblem>();
        }

        if (inProgress) TryRestoreFocus();
        if (changed) Notify();
    }

    private void Notify() => Changed?.Invoke();

    /// <summary>
    /// Best-effort focus restoration: logs the attempt with the stored handle,
    /// clears it, then invokes the <see cref="FocusRestore"/> hook (if any)
    /// inside try/catch so prerendering/SSR never throws.
    /// </summary>
    private void TryRestoreFocus()
    {
        string? handle;
        Action<string?>? restore;
        lock (_gate)
        {
            handle = _priorFocusHandle;
            restore = _focusRestore;
            _focusRestoreLog.Add(handle);
            _priorFocusHandle = null;
        }

        if (restore is null) return;
        try { restore(handle); }
        catch { /* Best effort: host focus work must never break tour state. */ }
    }

    private static void CheckField(string value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{field} must not be empty.", nameof(value));
        if (value.Length > max)
            throw new ArgumentException($"{field} must be at most {max} characters.", nameof(value));
    }
}
