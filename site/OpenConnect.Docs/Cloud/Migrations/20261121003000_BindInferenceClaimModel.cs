using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121003000_BindInferenceClaimModel")]
public sealed class BindInferenceClaimModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.execution_claim_inference_models (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.execution_claims ("ReservationId") ON DELETE RESTRICT,
            "ModelId" text NOT NULL CHECK (length("ModelId") BETWEEN 1 AND 256)
        );
        CREATE TRIGGER cloud_claim_inference_model_append_only BEFORE UPDATE OR DELETE ON cloud.execution_claim_inference_models
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        CREATE TRIGGER cloud_claim_inference_model_no_truncate BEFORE TRUNCATE ON cloud.execution_claim_inference_models
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain inference claim model evidence when rolling back application code.");
}
