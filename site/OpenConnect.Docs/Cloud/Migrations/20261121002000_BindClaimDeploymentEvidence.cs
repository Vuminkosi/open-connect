using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121002000_BindClaimDeploymentEvidence")]
public sealed class BindClaimDeploymentEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.execution_claim_deployment_evidence (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.execution_claims ("ReservationId") ON DELETE RESTRICT,
            "EndpointReference" text NOT NULL,
            "Transport" text NOT NULL,
            "ContractVersion" text NOT NULL,
            "ModelId" text NOT NULL,
            "ModelRevision" text NOT NULL,
            "ModelDigest" text NOT NULL,
            "ImageDigest" text NOT NULL
        );
        CREATE TRIGGER cloud_claim_deployment_evidence_append_only BEFORE UPDATE OR DELETE ON cloud.execution_claim_deployment_evidence
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        CREATE TRIGGER cloud_claim_deployment_evidence_no_truncate BEFORE TRUNCATE ON cloud.execution_claim_deployment_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain claim deployment evidence when rolling back application code.");
}
