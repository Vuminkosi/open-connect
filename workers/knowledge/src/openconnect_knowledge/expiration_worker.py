"""Opt-in expiry maintenance, independent of model and object-store availability."""

from __future__ import annotations

import os
import signal
import threading

from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex


def run(stop: threading.Event) -> None:
    """Process bounded expiry batches until shutdown; do not log protected data."""
    if os.environ.get("OC_INDEX_EXPIRY_ENABLED", "false").lower() != "true":
        raise ValueError("index_expiry_disabled")
    index = PostgresKnowledgeIndex(os.environ["OC_KNOWLEDGE_SEARCH_DSN"])
    after = None
    while not stop.is_set():
        try:
            tenants = index.expiration_tenants(after=after)
            if not tenants:
                after = None
                stop.wait(2)
                continue
            after = tenants[-1]
            expired = 0
            failed = False
            for tenant in tenants:
                if stop.is_set():
                    break
                try:
                    expired += index.expire_due(tenant)
                except Exception:
                    # Continue other tenants without exposing database error details.
                    failed = True
            if failed:
                stop.wait(5)
            elif not expired:
                stop.wait(2)
        except Exception:
            # Database errors may contain credentials or protected identifiers.
            stop.wait(5)


def main() -> None:
    stop = threading.Event()
    for name in ("SIGINT", "SIGTERM"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), lambda *_: stop.set())
    try:
        run(stop)
    except Exception:
        raise SystemExit("index_expiry_unavailable") from None


if __name__ == "__main__":
    main()
