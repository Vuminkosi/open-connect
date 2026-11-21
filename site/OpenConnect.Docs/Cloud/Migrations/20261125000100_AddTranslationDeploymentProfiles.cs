using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261125000100_AddTranslationDeploymentProfiles")]
public sealed class AddTranslationDeploymentProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.translation_deployment_profiles (
            "DeploymentId" uuid PRIMARY KEY REFERENCES cloud.worker_deployments ("Id") ON DELETE RESTRICT,
            "DeploymentRevision" bigint NOT NULL CHECK ("DeploymentRevision" > 0),
            "Revision" bigint NOT NULL CHECK ("Revision" > 0),
            "TokenizerHash" varchar(64) NOT NULL CHECK ("TokenizerHash" ~ '^[0-9a-f]{64}$')
        );
        CREATE TABLE cloud.translation_deployment_pairs (
            "DeploymentId" uuid NOT NULL REFERENCES cloud.translation_deployment_profiles ("DeploymentId") ON DELETE CASCADE,
            "SourceLang" varchar(8) NOT NULL CHECK ("SourceLang" ~ '^[a-z]{3}_[A-Z][a-z]{3}$'),
            "TargetLang" varchar(8) NOT NULL CHECK ("TargetLang" ~ '^[a-z]{3}_[A-Z][a-z]{3}$'),
            CHECK ("SourceLang" <> "TargetLang"), PRIMARY KEY ("DeploymentId", "SourceLang", "TargetLang")
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Translation deployment profiles must be retained during application rollback.");
}
