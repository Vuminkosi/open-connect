-- Durable metadata-only indexing jobs. The object itself remains in private storage.
BEGIN;
CREATE TABLE openconnect_knowledge.index_jobs (
    tenant_id text NOT NULL CHECK (length(tenant_id) BETWEEN 1 AND 256),
    job_id uuid NOT NULL,
    payload jsonb NOT NULL,
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    state text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'leased', 'completed', 'dead')),
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    available_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    lease_id uuid,
    lease_until timestamptz,
    fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
    result_generation bigint CHECK (result_generation > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, job_id),
    CHECK ((state = 'leased' AND lease_id IS NOT NULL AND lease_until IS NOT NULL)
        OR (state <> 'leased' AND lease_id IS NULL AND lease_until IS NULL)),
    CHECK ((state = 'completed') = (result_generation IS NOT NULL))
);
CREATE INDEX knowledge_index_jobs_ready ON openconnect_knowledge.index_jobs
    (tenant_id, available_at, created_at) WHERE state IN ('pending', 'leased');
COMMIT;
