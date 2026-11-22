-- Derived image vectors inherit authorization and lifecycle from their parent document.
BEGIN;
CREATE TABLE openconnect_knowledge.image_vectors (
    tenant_id text NOT NULL,
    application_id text NOT NULL,
    collection_id text NOT NULL,
    document_id text NOT NULL,
    image_id text NOT NULL CHECK (length(image_id) BETWEEN 1 AND 256),
    image_revision text NOT NULL CHECK (length(image_revision) BETWEEN 1 AND 256),
    document_generation bigint NOT NULL CHECK (document_generation > 0),
    model_id text NOT NULL CHECK (length(model_id) BETWEEN 1 AND 256),
    model_fingerprint text NOT NULL CHECK (model_fingerprint ~ '^[0-9a-f]{64}$'),
    embedding_space_id text NOT NULL CHECK (embedding_space_id ~ '^[0-9a-f]{64}$'),
    embedding double precision[] NOT NULL CHECK (cardinality(embedding) BETWEEN 1 AND 8192),
    PRIMARY KEY (tenant_id, application_id, collection_id, document_id, image_id),
    FOREIGN KEY (tenant_id, application_id, collection_id, document_id)
        REFERENCES openconnect_knowledge.documents (tenant_id, application_id, collection_id, document_id)
        ON DELETE CASCADE
);
CREATE INDEX knowledge_image_vectors_space ON openconnect_knowledge.image_vectors
    (tenant_id, application_id, collection_id, embedding_space_id, model_id, model_fingerprint);
COMMIT;
