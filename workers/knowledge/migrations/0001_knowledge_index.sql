-- Applied explicitly to an application-controlled PostgreSQL database.
-- Customer content, vectors and ACLs are sensitive and require protected storage.
BEGIN;

CREATE SCHEMA openconnect_knowledge;

CREATE TABLE openconnect_knowledge.documents (
    tenant_id text NOT NULL CHECK (length(tenant_id) BETWEEN 1 AND 256),
    application_id text NOT NULL CHECK (length(application_id) BETWEEN 1 AND 256),
    collection_id text NOT NULL CHECK (length(collection_id) BETWEEN 1 AND 256),
    document_id text NOT NULL CHECK (length(document_id) BETWEEN 1 AND 256),
    generation bigint NOT NULL CHECK (generation >= 0),
    deleted boolean NOT NULL,
    source_revision text,
    embedding_space_id text,
    model_id text,
    model_fingerprint text,
    dimension integer,
    manifest_sha256 text,
    allowed_principals text[] NOT NULL DEFAULT '{}',
    allowed_roles text[] NOT NULL DEFAULT '{}',
    expires_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, application_id, collection_id, document_id),
    CHECK (array_position(allowed_principals, NULL) IS NULL AND array_position(allowed_roles, NULL) IS NULL),
    CHECK (deleted OR (
        source_revision IS NOT NULL AND length(source_revision) BETWEEN 1 AND 256
        AND embedding_space_id IS NOT NULL AND embedding_space_id ~ '^[0-9a-f]{64}$'
        AND model_id IS NOT NULL AND length(model_id) BETWEEN 1 AND 256
        AND model_fingerprint IS NOT NULL AND model_fingerprint ~ '^[0-9a-f]{64}$'
        AND dimension IS NOT NULL AND dimension BETWEEN 1 AND 8192
        AND manifest_sha256 IS NOT NULL AND manifest_sha256 ~ '^[0-9a-f]{64}$'
        AND (cardinality(allowed_principals) + cardinality(allowed_roles)) > 0
    ))
);

CREATE TABLE openconnect_knowledge.chunks (
    tenant_id text NOT NULL,
    application_id text NOT NULL,
    collection_id text NOT NULL,
    document_id text NOT NULL,
    chunk_id text NOT NULL CHECK (chunk_id ~ '^[0-9a-f]{64}$'),
    page_id text NOT NULL CHECK (length(page_id) BETWEEN 1 AND 256),
    page_index integer NOT NULL CHECK (page_index BETWEEN 0 AND 29),
    source_start integer NOT NULL CHECK (source_start >= 0),
    source_end integer NOT NULL CHECK (source_end > source_start),
    content text NOT NULL CHECK (length(content) BETWEEN 1 AND 8192),
    content_sha256 text NOT NULL CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    embedding double precision[] NOT NULL CHECK (cardinality(embedding) BETWEEN 1 AND 8192),
    search_text tsvector GENERATED ALWAYS AS (to_tsvector('simple'::regconfig, content)) STORED,
    PRIMARY KEY (tenant_id, application_id, collection_id, document_id, chunk_id),
    FOREIGN KEY (tenant_id, application_id, collection_id, document_id)
        REFERENCES openconnect_knowledge.documents (tenant_id, application_id, collection_id, document_id)
        ON DELETE CASCADE
);

CREATE INDEX knowledge_chunks_lexical ON openconnect_knowledge.chunks USING gin (search_text);

-- Metadata only: a consumer reconciles dependent stores after a committed change.
CREATE TABLE openconnect_knowledge.outbox (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id text NOT NULL,
    application_id text NOT NULL,
    collection_id text NOT NULL,
    document_id text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    operation text NOT NULL CHECK (operation IN ('upsert', 'delete')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (tenant_id, application_id, collection_id, document_id, generation, operation),
    FOREIGN KEY (tenant_id, application_id, collection_id, document_id)
        REFERENCES openconnect_knowledge.documents (tenant_id, application_id, collection_id, document_id)
);

COMMIT;
