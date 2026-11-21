using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121235958_AddWorkerExpansionCatalog")]
public sealed class AddWorkerExpansionCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        INSERT INTO cloud.capabilities ("Id", "Name", "Description", "ContractVersion", "BillingUnit", "PlatformAvailability",
            "Dependencies", "Modalities", "ModalitiesVerified", "AllowedRegions", "AllowedRegionsVerified") VALUES
            ('documents.analyze', 'Document analysis', 'OCR and document analysis.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('knowledge.embed', 'Text embeddings', 'Text embeddings for retrieval.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('knowledge.rerank', 'Relevance reranking', 'Rerank authorized candidate passages.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('vision.analyze', 'Image analysis', 'Image detection and classification.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('analytics.analyze', 'Analytics', 'Statistical summaries, anomalies and forecasts.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('knowledge.index', 'Knowledge indexing', 'Index and manage authorized documents.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('documents.extract', 'Structured extraction', 'Extract schema-constrained values from text.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false),
            ('vision.embed', 'Image embeddings', 'Image and text embeddings for visual retrieval.', 'openconnect.v1', 'not configured', 'not_configured', '[]', '[]', false, '[]', false)
        ON CONFLICT ("Id") DO NOTHING;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Preserve capability configuration and entitlements when rolling back application code.");
}
