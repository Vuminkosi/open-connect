-- Adds lease state without changing existing metadata-only events.
BEGIN;
ALTER TABLE openconnect_knowledge.outbox
    ADD COLUMN delivery_state text NOT NULL DEFAULT 'pending'
        CHECK (delivery_state IN ('pending', 'leased', 'delivered', 'dead')),
    ADD COLUMN available_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    ADD COLUMN lease_token uuid,
    ADD COLUMN lease_until timestamptz,
    ADD COLUMN attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    ADD COLUMN delivered_at timestamptz,
    ADD CONSTRAINT knowledge_outbox_lease_shape CHECK (
        (delivery_state = 'leased' AND lease_token IS NOT NULL AND lease_until IS NOT NULL)
        OR (delivery_state <> 'leased' AND lease_token IS NULL AND lease_until IS NULL)
    ),
    ADD CONSTRAINT knowledge_outbox_delivery_shape CHECK (
        (delivery_state = 'delivered') = (delivered_at IS NOT NULL)
    );
CREATE INDEX knowledge_outbox_pending ON openconnect_knowledge.outbox
    (tenant_id, available_at, id) WHERE delivery_state IN ('pending', 'leased');
CREATE INDEX knowledge_outbox_document_order ON openconnect_knowledge.outbox
    (tenant_id, application_id, collection_id, document_id, generation)
    WHERE delivery_state <> 'delivered';
COMMIT;
