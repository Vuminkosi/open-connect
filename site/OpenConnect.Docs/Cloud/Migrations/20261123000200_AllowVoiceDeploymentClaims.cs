using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261123000200_AllowVoiceDeploymentClaims")]
public sealed class AllowVoiceDeploymentClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.execution_claims, cloud.worker_deployments IN SHARE ROW EXCLUSIVE MODE;
        DO $voice_migration$
        DECLARE definition text;
        DECLARE updated text;
        BEGIN
            SELECT pg_get_functiondef('cloud.guard_deployment_concurrency()'::regprocedure) INTO definition;
            IF position($voice$'voice.synthesize'$voice$ in definition) > 0 THEN RETURN; END IF;
            IF length(definition) - length(replace(definition, $old$'inference.liveui')$old$, '')) <> length($old$'inference.liveui')$old$) THEN
                RAISE EXCEPTION 'deployment guard has an unexpected definition; review capability support before migration';
            END IF;
            updated := replace(definition, $old$'inference.liveui')$old$, $new$'inference.liveui', 'voice.synthesize')$new$);
            IF updated = definition THEN
                RAISE EXCEPTION 'deployment guard has an unexpected definition; review capability support before migration';
            END IF;
            EXECUTE updated;
        END;
        $voice_migration$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain deployment concurrency protection when rolling back application code.");
}
