using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121001000_BindExecutionClaimCredential")]
public sealed class BindExecutionClaimCredential : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.execution_claim_credentials (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.execution_claims ("ReservationId") ON DELETE RESTRICT,
            "CredentialId" uuid NOT NULL REFERENCES cloud.credentials ("Id") ON DELETE RESTRICT
        );
        CREATE TRIGGER cloud_claim_credential_append_only BEFORE UPDATE OR DELETE ON cloud.execution_claim_credentials
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        CREATE TRIGGER cloud_claim_credential_no_truncate BEFORE TRUNCATE ON cloud.execution_claim_credentials
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain claim credential bindings when rolling back application code.");
}
