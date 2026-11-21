"""Opt-in index job consumer; startup never downloads model assets."""

from __future__ import annotations

import os
import signal
import threading
from pathlib import Path
from typing import Callable
from uuid import uuid4

from minio import Minio

from openconnect_knowledge.app import _load_engine
from openconnect_knowledge.document_parser import DocumentOcrParser
from openconnect_knowledge.index_jobs import PostgresIndexJobs
from openconnect_knowledge.index_processor import IndexStatus, process_one
from openconnect_knowledge.minio_resolver import MinioResolver, storage_pool
from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex
from openconnect_worker_common.security.clamav import ClamAvScanner
from openconnect_worker_common.security.signing import key_from_environment


def _wait_for_initial_readiness(stop: threading.Event, heartbeat: Callable[[], None]) -> bool:
    """Wait without advertising or claiming work until dependencies are ready."""
    while not stop.is_set():
        try:
            heartbeat()
            return not stop.is_set()
        except Exception:
            # Dependency errors may contain credentials or customer information.
            stop.wait(5)
    return False


def _refresh_readiness(ready: threading.Event, heartbeat: Callable[[], None], retire: Callable[[], None]) -> None:
    """Suspend claims on failed probes and best-effort revoke the advertised lease."""
    try:
        heartbeat()
    except Exception:
        ready.clear()
        try:
            retire()
        except Exception:
            # A database outage prevents immediate revocation; the lease still expires.
            pass
    else:
        ready.set()


def run(stop: threading.Event) -> None:
    """Run until shutdown; the operator provisions models, migrations and secrets."""
    if os.environ.get("OC_INDEX_WORKER_ENABLED", "false").lower() != "true":
        raise ValueError("index_worker_disabled")
    tls = os.environ.get("OC_INDEX_MINIO_TLS", "true").lower()
    if tls not in {"true", "false"}:
        raise ValueError("invalid_index_configuration")
    endpoint = os.environ["OC_INDEX_MINIO_ENDPOINT"]
    if not endpoint or "://" in endpoint or "@" in endpoint or "/" in endpoint:
        raise ValueError("invalid_index_configuration")
    token = os.environ["OPENCONNECT_WORKER_TOKEN"]
    key = key_from_environment()
    if key is None or len(key) < 32:
        raise ValueError("invalid_index_configuration")
    dsn = os.environ["OC_KNOWLEDGE_SEARCH_DSN"]
    index = PostgresKnowledgeIndex(dsn)
    index._verify()
    jobs = PostgresIndexJobs(dsn)
    jobs.ready_tenants(limit=1)
    scanner = ClamAvScanner(Path(os.environ["OC_INDEX_CLAM_SOCKET"]))
    parser = DocumentOcrParser(Path(os.environ["OC_INDEX_DOCUMENT_SOCKET"]), token, key)
    engine = _load_engine()
    if engine is None:
        raise ValueError("model_unavailable")
    pool = storage_pool(tls=tls == "true", ca_file=os.environ.get("OC_INDEX_MINIO_CA_FILE"))
    client = Minio(endpoint, access_key=os.environ["OC_INDEX_MINIO_ACCESS_KEY"],
                   secret_key=os.environ["OC_INDEX_MINIO_SECRET_KEY"], secure=tls == "true", http_client=pool)
    bucket = os.environ["OC_INDEX_MINIO_BUCKET"]
    resolver = MinioResolver(client, bucket)
    worker_id = uuid4()
    space = engine.embed(["index worker readiness"], input_kind="query").space_id

    def heartbeat() -> None:
        scanner.scan(b"OpenConnect readiness probe", timeout_seconds=2)
        if not parser.ready() or not client.bucket_exists(bucket) or client.get_bucket_versioning(bucket).status != "Enabled":
            raise RuntimeError("index_dependency_unavailable")
        engine._verify()
        if not stop.is_set():
            jobs.record_worker(worker_id, space)

    ready = threading.Event()

    def heartbeat_loop() -> None:
        while not stop.wait(20):
            _refresh_readiness(ready, heartbeat, lambda: jobs.retire_worker(worker_id))

    heartbeat_thread = None
    try:
        if not _wait_for_initial_readiness(stop, heartbeat):
            return
        ready.set()
        heartbeat_thread = threading.Thread(target=heartbeat_loop, name="index-readiness", daemon=True)
        heartbeat_thread.start()
        after = None
        while not stop.is_set():
            try:
                if not ready.is_set():
                    stop.wait(2)
                    continue
                tenants = jobs.ready_tenants(embedding_space_id=space, after=after)
                if not tenants:
                    after = None
                    stop.wait(2)
                    continue
                after = tenants[-1]
                processed = False
                failed = False
                for tenant in tenants:
                    if stop.is_set() or not ready.is_set():
                        break
                    try:
                        result = process_one(jobs, index, tenant, resolver=resolver, scanner=scanner,
                                             parser=parser, embedder=engine, embedding_space_id=space, lease_seconds=300)
                        processed |= result is not IndexStatus.IDLE
                    except Exception:
                        # Isolate the tenant and do not expose dependency error details.
                        failed = True
                if failed:
                    stop.wait(5)
                elif not processed:
                    stop.wait(2)
            except Exception:
                # Error messages may contain credentials or customer data.
                stop.wait(5)
    finally:
        stop.set()
        if heartbeat_thread is not None:
            heartbeat_thread.join(timeout=35)
        if heartbeat_thread is None or not heartbeat_thread.is_alive():
            try:
                jobs.retire_worker(worker_id)
            except Exception:
                pass
        pool.clear()


def main() -> None:
    stop = threading.Event()
    for name in ("SIGINT", "SIGTERM"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), lambda *_: stop.set())
    try:
        run(stop)
    except Exception:
        raise SystemExit("index_worker_unavailable") from None


if __name__ == "__main__":
    main()
