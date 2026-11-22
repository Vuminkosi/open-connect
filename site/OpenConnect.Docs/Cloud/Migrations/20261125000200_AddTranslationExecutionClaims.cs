using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261125000200_AddTranslationExecutionClaims")]
public sealed class AddTranslationExecutionClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.execution_claims, cloud.worker_deployments IN SHARE ROW EXCLUSIVE MODE;
        DO $translation_migration$
        DECLARE definition text;
        BEGIN
            SELECT pg_get_functiondef('cloud.guard_deployment_concurrency()'::regprocedure) INTO definition;
            IF position($capability$'translation.translate'$capability$ in definition) > 0 THEN RETURN; END IF;
            IF length(definition) - length(replace(definition, $old$'voice.synthesize'$old$, '')) <> length($old$'voice.synthesize'$old$) THEN
                RAISE EXCEPTION 'deployment guard has an unexpected definition; review capability support before migration';
            END IF;
            EXECUTE replace(definition, $old$'voice.synthesize'$old$, $new$'voice.synthesize', 'translation.translate'$new$);
        END;
        $translation_migration$;
        CREATE TABLE cloud.translation_claim_evidence (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.execution_claims ("ReservationId") ON DELETE RESTRICT,
            "DeploymentRevision" bigint NOT NULL CHECK ("DeploymentRevision" > 0),
            "ProfileRevision" bigint NOT NULL CHECK ("ProfileRevision" > 0),
            "TokenizerHash" varchar(64) NOT NULL CHECK ("TokenizerHash" ~ '^[0-9a-f]{64}$'),
            "SourceLang" varchar(8) NOT NULL CHECK ("SourceLang" ~ '^[a-z]{3}_[A-Z][a-z]{3}$'),
            "TargetLang" varchar(8) NOT NULL CHECK ("TargetLang" ~ '^[a-z]{3}_[A-Z][a-z]{3}$'),
            CHECK ("SourceLang" <> "TargetLang")
        );
        CREATE TRIGGER cloud_translation_claim_evidence_append_only BEFORE UPDATE OR DELETE ON cloud.translation_claim_evidence
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        CREATE TRIGGER cloud_translation_claim_evidence_no_truncate BEFORE TRUNCATE ON cloud.translation_claim_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain translation claim evidence and deployment concurrency protection on rollback.");
}
