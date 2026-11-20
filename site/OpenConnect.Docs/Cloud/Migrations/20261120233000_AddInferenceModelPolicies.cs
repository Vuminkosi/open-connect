using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261120233000_AddInferenceModelPolicies")]
public sealed class AddInferenceModelPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.inference_model_policies (
            "TenantId" uuid PRIMARY KEY REFERENCES cloud.tenants ("Id") ON DELETE RESTRICT,
            "AllowedModels" jsonb NOT NULL CHECK (jsonb_typeof("AllowedModels") = 'array'),
            "Revision" integer NOT NULL CHECK ("Revision" > 0)
        );
        CREATE FUNCTION cloud.advance_inference_model_policy_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."Revision" = 2147483647 THEN RAISE EXCEPTION 'policy revision exhausted' USING ERRCODE = '23514'; END IF;
            NEW."Revision" := OLD."Revision" + 1;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_inference_model_policy_revision BEFORE UPDATE ON cloud.inference_model_policies
            FOR EACH ROW EXECUTE FUNCTION cloud.advance_inference_model_policy_revision();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Inference model policies must be retained during application rollback.");
}
