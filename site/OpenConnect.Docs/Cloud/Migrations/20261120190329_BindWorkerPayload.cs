using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class BindWorkerPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "WorkerPayloadFingerprint",
                schema: "cloud",
                table: "execution_claims",
                type: "bytea",
                nullable: true);
            migrationBuilder.Sql("ALTER TABLE cloud.execution_claims ADD CONSTRAINT ck_worker_payload_fingerprint_length CHECK (\"WorkerPayloadFingerprint\" IS NULL OR octet_length(\"WorkerPayloadFingerprint\") = 32)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Worker payload binding is immutable; roll back application code without dropping claim evidence.");
        }
    }
}
