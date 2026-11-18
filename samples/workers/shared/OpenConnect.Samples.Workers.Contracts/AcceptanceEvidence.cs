using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenConnect.Samples.Workers.Contracts;

public sealed record AcceptanceEvidenceRecord
{
    public const string CanonicalSchemaVersion = "openconnect.samples.workers.evidence/v1";

    public static readonly IReadOnlySet<string> AllowedStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "NOT_RUN",
        "PASS",
        "FAIL",
        "SKIPPED",
        "BLOCKED"
    };

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = CanonicalSchemaVersion;

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("worker")]
    public required string Worker { get; init; }

    [JsonPropertyName("tier")]
    public required string Tier { get; init; }

    [JsonPropertyName("acceptanceId")]
    public required string AcceptanceId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "NOT_RUN";

    [JsonPropertyName("sourceCommit")]
    public required string SourceCommit { get; init; }

    [JsonPropertyName("imageDigest")]
    public required string ImageDigest { get; init; }

    [JsonPropertyName("modelFingerprint")]
    public string? ModelFingerprint { get; init; }

    [JsonPropertyName("databaseProvider")]
    public required string DatabaseProvider { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("evidenceUri")]
    public required string EvidenceUri { get; init; }

    [JsonPropertyName("reviewId")]
    public string? ReviewId { get; init; }

    [JsonPropertyName("observedAtUtc")]
    public string? ObservedAtUtc { get; init; }

    public void Validate()
    {
        if (SchemaVersion != CanonicalSchemaVersion)
            throw new InvalidOperationException($"Invalid evidence schemaVersion '{SchemaVersion}'.");
        if (!AllowedStatuses.Contains(Status))
            throw new InvalidOperationException($"Invalid evidence status '{Status}'.");
        if (string.IsNullOrWhiteSpace(RunId) || string.IsNullOrWhiteSpace(Worker) ||
            string.IsNullOrWhiteSpace(Tier) || string.IsNullOrWhiteSpace(AcceptanceId) ||
            string.IsNullOrWhiteSpace(SourceCommit) || string.IsNullOrWhiteSpace(ImageDigest) ||
            string.IsNullOrWhiteSpace(DatabaseProvider) || string.IsNullOrWhiteSpace(Command) ||
            string.IsNullOrWhiteSpace(EvidenceUri))
        {
            throw new InvalidOperationException("Required evidence fields must be non-empty.");
        }
        if (Status == "PASS" && string.IsNullOrWhiteSpace(ObservedAtUtc))
        {
            throw new InvalidOperationException("PASS status requires observedAtUtc timestamp from an actual test execution.");
        }
    }
}

public static partial class EvidenceGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [GeneratedRegex(@"(?i)(Bearer\s+[A-Za-z0-9._\-]+|sk-[A-Za-z0-9]{8,}|OPENCONNECT_SCAN_TOKEN=[^\s""']+|password=[^\s;""']+)")]
    private static partial Regex SensitiveTokenRegex();

    public static string SanitizeText(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;
        return SensitiveTokenRegex().Replace(input, "[REDACTED]");
    }

    public static IReadOnlyList<AcceptanceEvidenceRecord> BuildInitialCatalogue(
        string runId,
        string sourceCommit,
        string databaseProvider = "sqlite",
        string tier = "integration")
    {
        var list = new List<AcceptanceEvidenceRecord>(80);
        foreach (var worker in CanonicalWorkers.All)
        {
            foreach (var atId in worker.AcceptanceIds)
            {
                list.Add(new AcceptanceEvidenceRecord
                {
                    RunId = runId,
                    Worker = worker.WorkerId,
                    Tier = tier,
                    AcceptanceId = atId,
                    Status = "NOT_RUN",
                    SourceCommit = sourceCommit,
                    ImageDigest = $"local:e2e-{sourceCommit}",
                    ModelFingerprint = null,
                    DatabaseProvider = databaseProvider,
                    Command = $"./samples/workers/shared/scripts/run-one.sh {worker.WorkerId} --tier {tier} --provider {databaseProvider}",
                    EvidenceUri = $"samples/workers/shared/evidence/{runId}/{worker.WorkerId}/{atId}.json",
                    ReviewId = null,
                    ObservedAtUtc = null
                });
            }
        }
        return list;
    }

    public static void WriteEvidenceBundle(
        string outputDirectory,
        IReadOnlyList<AcceptanceEvidenceRecord> records,
        string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
        {
            record.Validate();
        }

        Directory.CreateDirectory(outputDirectory);
        var resultJsonPath = Path.Combine(outputDirectory, "result.json");
        var summaryMdPath = Path.Combine(outputDirectory, "summary.md");

        var json = JsonSerializer.Serialize(records, JsonOptions);
        File.WriteAllText(resultJsonPath, SanitizeText(json), Encoding.UTF8);

        var sb = new StringBuilder();
        sb.AppendLine("# OpenConnect Worker Samples E2E Evidence Summary");
        sb.AppendLine();
        if (records.Count > 0)
        {
            sb.AppendLine($"- **Run ID**: `{SanitizeText(records[0].RunId)}`");
            sb.AppendLine($"- **Source Commit**: `{SanitizeText(records[0].SourceCommit)}`");
            sb.AppendLine($"- **Database Provider**: `{SanitizeText(records[0].DatabaseProvider)}`");
            sb.AppendLine($"- **Tier**: `{SanitizeText(records[0].Tier)}`");
        }
        if (!string.IsNullOrWhiteSpace(notes))
        {
            sb.AppendLine($"- **Notes**: {SanitizeText(notes)}");
        }
        sb.AppendLine();
        sb.AppendLine("| Acceptance ID | Worker | Tier | Provider | Status | Evidence URI |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in records)
        {
            sb.AppendLine($"| `{r.AcceptanceId}` | `{r.Worker}` | `{r.Tier}` | `{r.DatabaseProvider}` | `{r.Status}` | `{SanitizeText(r.EvidenceUri)}` |");
        }

        File.WriteAllText(summaryMdPath, sb.ToString(), Encoding.UTF8);
    }
}
