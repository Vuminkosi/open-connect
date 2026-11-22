-- Bounded expiry scans avoid retaining expired indexed content indefinitely.
BEGIN;
CREATE INDEX knowledge_documents_expiration ON openconnect_knowledge.documents
    (expires_at, tenant_id) WHERE NOT deleted AND expires_at IS NOT NULL;
COMMIT;
