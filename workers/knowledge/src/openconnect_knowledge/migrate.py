"""Explicit, checksum-verified migration runner for a new knowledge schema."""

from __future__ import annotations

import hashlib
import os
import re
import sys
from pathlib import Path

import psycopg

_NAME = re.compile(r"^[0-9]{4}_[a-z0-9_]+\.sql$")
_LOCK = 5570744146781338625


def load_migrations(directory: Path) -> list[tuple[str, str, str]]:
    """Read the complete immutable sequence and remove its outer transaction wrapper."""
    files = sorted(directory.glob("*.sql"))
    if not files or any(not _NAME.fullmatch(path.name) for path in files):
        raise ValueError("invalid_migration_set")
    numbers = [int(path.name[:4]) for path in files]
    if numbers != list(range(1, len(files) + 1)):
        raise ValueError("invalid_migration_sequence")
    result = []
    for path in files:
        raw = path.read_bytes()
        if len(raw) > 1024 * 1024:
            raise ValueError("invalid_migration")
        text = raw.decode("utf-8")
        body = re.sub(r"\A(?:\s|--[^\n]*(?:\n|$))*BEGIN\s*;", "", text, count=1, flags=re.I)
        body, removed = re.subn(r"COMMIT\s*;\s*\Z", "", body, count=1, flags=re.I)
        if body == text or removed != 1 or re.search(r"(?im)(?:^|;)\s*(?:BEGIN|COMMIT|ROLLBACK)\s*;", body):
            raise ValueError("invalid_migration_transaction")
        result.append((path.name, hashlib.sha256(raw).hexdigest(), body))
    return result


def migrate(dsn: str, directory: Path) -> int:
    """Apply all pending migrations atomically; never adopt an untracked schema."""
    if not isinstance(dsn, str) or not dsn.strip():
        raise ValueError("invalid_database_configuration")
    migrations = load_migrations(directory)
    with psycopg.connect(dsn, connect_timeout=5) as connection:
        connection.execute("SET LOCAL statement_timeout = '60s'")
        connection.execute("SELECT pg_advisory_xact_lock(%s)", (_LOCK,))
        tracked, schema = connection.execute("SELECT to_regclass('public.openconnect_knowledge_migrations'), "
                                             "to_regnamespace('openconnect_knowledge')").fetchone()
        if tracked is None and schema is not None:
            raise RuntimeError("untracked_existing_schema")
        connection.execute("""CREATE TABLE IF NOT EXISTS public.openconnect_knowledge_migrations
            (name text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT clock_timestamp())""")
        rows = connection.execute("SELECT name, sha256 FROM public.openconnect_knowledge_migrations ORDER BY name").fetchall()
        expected = [(name, digest) for name, digest, _ in migrations]
        if rows != expected[:len(rows)] or bool(rows) != (schema is not None):
            raise RuntimeError("migration_history_mismatch")
        for name, digest, body in migrations[len(rows):]:
            connection.execute(body)
            connection.execute("INSERT INTO public.openconnect_knowledge_migrations (name, sha256) VALUES (%s, %s)",
                               (name, digest))
        for relation, columns in (
            ("documents", "tenant_id, generation, deleted, allowed_principals, allowed_roles, expires_at"),
            ("chunks", "tenant_id, content, embedding, search_text"),
            ("outbox", "tenant_id, generation, operation, delivery_state, lease_token"),
            ("index_jobs", "tenant_id, job_id, state, payload, lease_id, fencing_token"),
            ("index_workers", "worker_id, embedding_space_id, expires_at"),
            ("image_vectors", "tenant_id, document_generation, embedding"),
        ):
            connection.execute(f"SELECT {columns} FROM openconnect_knowledge.{relation} LIMIT 0")
    return len(migrations) - len(rows)


def main() -> int:
    try:
        count = migrate(os.environ.get("OC_KNOWLEDGE_MIGRATION_DSN", ""), Path("/app/workers/knowledge/migrations"))
    except Exception:
        print("knowledge_migration_failed", file=sys.stderr)
        return 1
    print(f"knowledge_migrations_applied={count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
