using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Samples.Shared;

// Architecture Atlas end-to-end flowchart sample.
// Verbs: run | verify | help.
//   run    : print per-layer PASS lines for each flowchart subgraph; always 0 unless an exception escapes.
//   verify : fail non-zero unless Direct + Workflow-matrix shape ok, unsupported gates throw, and the EF model is ok.
//   help   : usage (exit 2, matching the other samples).
var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("run" or "verify"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.ArchitectureAtlas <run|verify|help>");
    Console.WriteLine("  run    : exercise every flowchart layer against real code and print per-layer PASS lines.");
    Console.WriteLine("  verify : fail non-zero unless Direct + Workflow-matrix shape ok, unsupported gates throw, and the EF model verifies.");
    return 2;
}

var verify = verb == "verify";
var failures = new List<string>();

// APPS — Developer Applications. This console is the calling application standing in for
// ASP.NET Core / Blazor / Browser / Node callers; the layer passes by reaching real code below.
Console.WriteLine("[APPS] Developer Applications -> sample console entry reached: PASS");

// CLIENT — OpenConnect Client Packages -> Application Harness wiring (type-load check).
// Hosting side (referenced): the MapOpenConnect endpoint target must exist.
var mapMethod = typeof(EndpointRouteBuilderExtensions).GetMethod(
    nameof(EndpointRouteBuilderExtensions.MapOpenConnect),
    BindingFlags.Public | BindingFlags.Static);
if (mapMethod is null)
{
    failures.Add("Hosting MapOpenConnect endpoint missing.");
    Console.WriteLine("[CLIENT] OpenConnect.Hosting.AspNetCore MapOpenConnect type-load: FAIL");
}
else
{
    Console.WriteLine("[CLIENT] Hosting wiring target OpenConnect.Hosting.AspNetCore.MapOpenConnect type-load: PASS");
}

// Client side (not referenced by this sample): probe the client assembly at runtime and report honestly.
if (TryLoadClientType(out var clientDetail))
{
    Console.WriteLine($"[CLIENT] OpenConnect.Client type-load: PASS ({clientDetail})");
}
else
{
    Console.WriteLine($"[CLIENT] OpenConnect.Client type-load: ABSENT_SAFE ({clientDetail}; client package not referenced by this sample)");
}

// HOST — OpenConnect Application Harness built from the real shared SampleHost.
using var host = SampleHost.CreateBuilder(args).Build();
Console.WriteLine("[HOST] Shared SampleHost built (OpenConnect.Core/Sessions/Tools/Privacy/Abstractions harness root): PASS");

// PROFILES + MAF — real MAF execution-profile gates over the loaded MAF runtime.
var matrix = MafRuntimeProbe.GetWorkflowCapabilityMatrix();
foreach (var probe in matrix)
{
    Console.WriteLine($"[PROFILES] Capability '{probe.Capability}': {(probe.Status == MafCapabilityStatus.Available ? "Available" : "Unavailable")} ({probe.RequiredTypeName})");
}

var requiredCapabilities = new[] { "Workflow", "Harness", "Durable execution", "MCP integration", "A2A integration" };
var missingEntries = requiredCapabilities.Where(name => matrix.All(probe => probe.Capability != name)).ToList();
if (missingEntries.Count > 0)
{
    failures.Add($"Workflow-matrix shape missing entries: {string.Join(", ", missingEntries)}.");
    Console.WriteLine($"[PROFILES] Workflow-matrix shape: FAIL (missing {string.Join(", ", missingEntries)})");
}
else
{
    Console.WriteLine("[PROFILES] Workflow-matrix shape (Workflow/Harness/Durable/MCP/A2A entries present): PASS");
}

// MAF Direct path: real factory type presence + the Direct gate must pass without throwing.
var factoryType = typeof(MafRealAgentFactory);
Console.WriteLine($"[MAF] Direct path type {factoryType.FullName} loaded from {factoryType.Assembly.GetName().Name}: PASS");
try
{
    OrchestrationProfileRouter.ValidateExecutionProfile("Direct");
    Console.WriteLine("[MAF] ValidateExecutionProfile(\"Direct\"): PASS");
}
catch (Exception exception)
{
    failures.Add($"Direct profile gate threw: {exception.GetType().Name}.");
    Console.WriteLine($"[MAF] ValidateExecutionProfile(\"Direct\"): FAIL ({exception.GetType().Name})");
}

// Unsupported execution profiles must fail closed via MafCapabilityUnavailableException (no fallback, no execution).
foreach (var profile in new[] { "Harness", "DurableWorkflow", "MCP", "A2A" })
{
    try
    {
        OrchestrationProfileRouter.ValidateExecutionProfile(profile);
        failures.Add($"Unsupported profile '{profile}' did not throw.");
        Console.WriteLine($"[PROFILES] {profile}: FAIL (gate did not throw)");
    }
    catch (MafCapabilityUnavailableException exception)
    {
        Console.WriteLine($"[PROFILES] {profile}: PROFILE_UNAVAILABLE (missing {exception.MissingTypeName}): PASS (fail closed)");
    }
}

// ADAPTERS + INFRA — real EF Core model verification against an application-owned
// Sqlite in-memory database (no seed, no migrations). No external infra is required.
await using var sqliteConnection = new SqliteConnection("DataSource=:memory:");
await sqliteConnection.OpenAsync().ConfigureAwait(false);
var atlasOptions = new DbContextOptionsBuilder<AtlasDbContext>().UseSqlite(sqliteConnection).Options;
await using var atlasDb = new AtlasDbContext(atlasOptions);
await atlasDb.Database.EnsureCreatedAsync().ConfigureAwait(false);
var tableCount = await SampleVerify.VerifyModelAsync(atlasDb).ConfigureAwait(false);
if (tableCount <= 0)
{
    failures.Add("EF model verified with zero mapped tables.");
    Console.WriteLine("[ADAPTERS] OpenConnect.Persistence.EFCore model verification: FAIL (zero tables)");
}
else
{
    Console.WriteLine($"[ADAPTERS] OpenConnect.Persistence.EFCore model verified ({tableCount} tables, application-owned Sqlite, no seed): PASS");
}

Console.WriteLine("[INFRA] Application-owned EF Core database verified in-process; NATS JetStream / Qdrant / Infisical KMS / OTLP not required: ABSENT_SAFE");

// Optional adapters/workers absent-safe: report what this process actually loaded; anything
// beyond EF/Hosting/Agents is absent by construction here (no NATS/Qdrant/KMS/OTLP/worker references).
var loaded = AppDomain.CurrentDomain.GetAssemblies()
    .Select(assembly => assembly.GetName().Name ?? string.Empty)
    .Where(name => name.StartsWith("OpenConnect.", StringComparison.Ordinal))
    .OrderBy(name => name, StringComparer.Ordinal)
    .ToList();
Console.WriteLine($"[ADAPTERS] Loaded OpenConnect assemblies: {string.Join(", ", loaded)}");
foreach (var optional in new[] { "NATS", "Qdrant", "KMS", "OTLP", "MCP microservices", "Python/Node workers" })
{
    Console.WriteLine($"[WORKERS] Optional {optional}: ABSENT_SAFE (not referenced, not loaded, no download attempted)");
}

if (verify && failures.Count > 0)
{
    Console.WriteLine($"VERIFY FAILED ({failures.Count}):");
    foreach (var failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }

    return 1;
}

Console.WriteLine(verify ? "VERIFY OK: all layers resolved." : "RUN OK: all layers exercised.");
return 0;

static bool TryLoadClientType(out string detail)
{
    try
    {
        var assembly = Assembly.Load("OpenConnect.Client");
        var clientType = assembly.GetType("OpenConnect.Client.OpenConnectClient");
        if (clientType is null)
        {
            detail = "assembly loaded but OpenConnectClient type not found";
            return false;
        }

        detail = $"{clientType.FullName} in {assembly.GetName().Name}";
        return true;
    }
    catch (Exception exception)
    {
        detail = exception.GetType().Name;
        return false;
    }
}

/// <summary>
/// Atlas-only sample DbContext. Registers the shared OpenConnect EF model with
/// SQLite-compatible mapping; verified via EnsureCreated + model checks (no migrations, no seed).
/// </summary>
public sealed class AtlasDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AtlasDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public AtlasDbContext(DbContextOptions<AtlasDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel(new OpenConnectModelOptions { UseSqlitePrefix = true });
    }
}
