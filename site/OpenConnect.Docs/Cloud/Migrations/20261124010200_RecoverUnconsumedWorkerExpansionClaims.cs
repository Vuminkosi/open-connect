using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261124010200_RecoverUnconsumedWorkerExpansionClaims")]
public sealed class RecoverUnconsumedWorkerExpansionClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_token_reservations, cloud.execution_claims,
            cloud.execution_consumptions, cloud.execution_completions, cloud.execution_abandonments IN SHARE ROW EXCLUSIVE MODE;
        DO $worker_recovery$
        DECLARE definition text;
        DECLARE updated text;
        BEGIN
            SELECT pg_get_functiondef('cloud.validate_execution_abandonment()'::regprocedure) INTO definition;
            IF position($capability$'analytics.analyze'$capability$ in definition) > 0 OR
               position($capability$'knowledge.embed'$capability$ in definition) > 0 THEN
                RAISE EXCEPTION 'abandonment guard has an unexpected capability list';
            END IF;
            IF length(definition) - length(replace(definition, $old$b."CapabilityId" = 'classification.classify' AND b."Unit" = 'request'$old$, ''))
                <> length($old$b."CapabilityId" = 'classification.classify' AND b."Unit" = 'request'$old$) THEN
                RAISE EXCEPTION 'abandonment guard has an unexpected definition; review capability support before migration';
            END IF;
            updated := replace(definition,
                $old$b."CapabilityId" = 'classification.classify' AND b."Unit" = 'request'$old$,
                $new$b."CapabilityId" IN ('classification.classify', 'analytics.analyze', 'knowledge.embed') AND b."Unit" = 'request'$new$);
            EXECUTE updated;
        END;
        $worker_recovery$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain claim recovery protections during application rollback.");
}
