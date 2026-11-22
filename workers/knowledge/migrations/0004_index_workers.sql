-- Ephemeral readiness leases; expired entries are not considered available.
BEGIN;
CREATE TABLE openconnect_knowledge.index_workers (
    worker_id uuid PRIMARY KEY,
    embedding_space_id text NOT NULL CHECK (embedding_space_id ~ '^[0-9a-f]{64}$'),
    last_seen timestamptz NOT NULL DEFAULT clock_timestamp(),
    expires_at timestamptz NOT NULL
);
CREATE INDEX knowledge_index_workers_live ON openconnect_knowledge.index_workers (expires_at, embedding_space_id);
COMMIT;
