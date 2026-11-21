using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261124010100_AllowWorkerExpansionDeploymentClaims")]
public sealed class AllowWorkerExpansionDeploymentClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.execution_claims, cloud.worker_deployments IN SHARE ROW EXCLUSIVE MODE;
        DO $worker_expansion$
        DECLARE definition text;
        DECLARE updated text;
        BEGIN
            SELECT pg_get_functiondef('cloud.guard_deployment_concurrency()'::regprocedure) INTO definition;
            IF position($capability$'knowledge.embed'$capability$ in definition) > 0 OR
               position($capability$'analytics.analyze'$capability$ in definition) > 0 THEN
                RAISE EXCEPTION 'deployment guard has an unexpected capability list';
            END IF;
            IF length(definition) - length(replace(definition, $old$'voice.synthesize'$old$, '')) <> length($old$'voice.synthesize'$old$) THEN
                RAISE EXCEPTION 'deployment guard has an unexpected definition; review capability support before migration';
            END IF;
            updated := replace(definition, $old$'voice.synthesize'$old$, $new$'voice.synthesize', 'analytics.analyze', 'knowledge.embed'$new$);
            EXECUTE updated;
        END;
        $worker_expansion$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain deployment concurrency protection when rolling back application code.");
}
