using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Templates;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Run-scoped freeze + resume-compat evidence (CONFIG-016 SLICE-C7):
/// a run binds the catalog revision at start, resume on an incompatible
/// revision fails closed, and hot reload mutates nothing in flight.
/// Scope is revision-compat only; TurnDurableAsync gates (identity/binding/
/// deadline/revocation + turn-gate/CAS/rate/budget) are covered elsewhere.
/// Built only on existing seams (LeadResolver pinned-revision check,
/// AgentCatalog duplicate rejection); no src changes.
/// </summary>
public sealed class RunFreezeResumeTests
{
    private static readonly TaskId Task = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV"));

    [Fact]
    public void Run_captures_revision_at_start_and_inflight_snapshot_survives_catalog_mutation()
    {
        var lead = new AgentDefinition(new AgentId("lead"), "Lead");
        var resolver = new LeadResolver();
        var registration = new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id);

        // Run start: bind lead + catalog revision for this request.
        var bound = resolver.ResolveBound(Task, [lead], registration);
        Assert.Equal(Task, bound.BoundTaskId);
        Assert.Same(lead, bound.Lead);
        Assert.Equal(64, bound.CatalogRevision.Length);

        // Catalog mutates after run start (unrelated registration).
        var other = new AgentDefinition(new AgentId("other"), "Other");
        var mutated = resolver.ResolveBound(Task, [lead, other], registration);

        // The in-flight snapshot is untouched: revision, lead, and task binding are frozen.
        Assert.NotEqual(bound.CatalogRevision, mutated.CatalogRevision);
        Assert.Equal(64, mutated.CatalogRevision.Length);
        Assert.Same(lead, bound.Lead);
        Assert.Equal(Task, bound.BoundTaskId);
    }

    [Fact]
    public void Resume_with_same_revision_succeeds()
    {
        var lead = new AgentDefinition(new AgentId("lead"), "Lead");
        var resolver = new LeadResolver();
        var start = resolver.ResolveBound(Task, [lead], new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id));

        // Resume against the unchanged catalog with the run's pinned revision.
        var pinned = new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id, start.CatalogRevision);
        var (resumedLead, resumedRevision) = resolver.Resolve(Task, [lead], pinned);

        Assert.Same(lead, resumedLead);
        Assert.Equal(start.CatalogRevision, resumedRevision);
    }

    [Fact]
    public void Resume_with_changed_revision_fails_closed_before_any_execution()
    {
        var lead = new AgentDefinition(new AgentId("lead"), "Lead");
        var resolver = new LeadResolver();
        var start = resolver.ResolveBound(Task, [lead], new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id));

        // Catalog changed after the run started; resume pins the stale revision.
        var other = new AgentDefinition(new AgentId("other"), "Other");
        var stale = new AgentRegistrationExtensions.LeadAgentRegistration(lead.Id, start.CatalogRevision);

        // Settled synchronous failure: no execution is reachable past this gate.
        var failure = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(Task, [lead, other], stale));
        Assert.Contains("catalog changed", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hot_reload_reregister_leaves_inflight_snapshot_untouched()
    {
        var catalog = new AgentCatalog();
        var id = new AgentId("lead");
        catalog.Register(new AgentDefinition(id, "Lead") { Version = "1", PolicyRevision = "1" });
        var inflight = catalog.Get(id);
        Assert.NotNull(inflight);

        // Hot reload re-registration is rejected by the duplicate-id seam
        // (AgentCatalog.cs:21-22), regardless of policy content...
        Assert.Throws<AgentCatalogException>(() =>
            catalog.Register(new AgentDefinition(id, "Lead") { Version = "2", PolicyRevision = "99" }));

        // ...and the in-flight definition is untouched (same stored reference).
        var current = catalog.Get(id);
        Assert.NotNull(current);
        Assert.Same(inflight, current);
        Assert.Equal("1", current.Version);
        Assert.Equal("1", current.PolicyRevision);
    }
}
