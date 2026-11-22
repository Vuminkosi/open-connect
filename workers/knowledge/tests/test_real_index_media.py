"""Opt-in disposable MinIO, ClamAV and PostgreSQL pipeline acceptance."""

import hashlib
import io
import os
import secrets
from pathlib import Path
from uuid import uuid4

import pytest
from psycopg.conninfo import conninfo_to_dict

from openconnect_knowledge.chunking import DocumentScope, SourcePage
from openconnect_knowledge.embeddings import Embedding, EmbeddingBatch
from openconnect_knowledge.index_jobs import IndexJob, PostgresIndexJobs
from openconnect_knowledge.index_processor import IndexStatus, process_one
from openconnect_knowledge.minio_resolver import MinioResolver
from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex, SearchScope
from openconnect_worker_common.security.clamav import ClamAvScanner

_NAMES = ["OC_TEST_KNOWLEDGE_PG_DSN", "OC_TEST_INDEX_MINIO_ENDPOINT", "OC_TEST_INDEX_MINIO_ACCESS",
          "OC_TEST_INDEX_MINIO_SECRET", "OC_TEST_INDEX_CLAM_SOCKET"]
pytestmark = pytest.mark.skipif(any(not os.environ.get(name) for name in _NAMES),
                                reason="Requires explicitly configured disposable media services")


def test_versioned_object_scan_and_eicar_rejection():
    minio = pytest.importorskip("minio")
    versioning = pytest.importorskip("minio.versioningconfig")
    dsn = os.environ["OC_TEST_KNOWLEDGE_PG_DSN"]
    settings = conninfo_to_dict(dsn)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    endpoint = os.environ["OC_TEST_INDEX_MINIO_ENDPOINT"]
    assert endpoint.startswith(("127.0.0.1:", "localhost:"))
    client = minio.Minio(endpoint, access_key=os.environ["OC_TEST_INDEX_MINIO_ACCESS"],
                         secret_key=os.environ["OC_TEST_INDEX_MINIO_SECRET"], secure=False)
    bucket = "index-test-" + secrets.token_hex(6)
    client.make_bucket(bucket)
    client.set_bucket_versioning(bucket, versioning.VersioningConfig("Enabled"))
    tenant = "0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25))
    jobs, index = PostgresIndexJobs(dsn), PostgresKnowledgeIndex(dsn)
    resolver = MinioResolver(client, bucket)
    scanner = ClamAvScanner(Path(os.environ["OC_TEST_INDEX_CLAM_SOCKET"]))

    class Parser:
        def parse(self, tenant_id, content):
            assert tenant_id == tenant
            return [SourcePage("page", content.decode("ascii"))]

    class Engine:
        def embed(self, texts, *, input_kind):
            assert input_kind == "passage"
            return EmbeddingBatch(tuple(Embedding((1.0, 0.0), False, 1.0) for _ in texts),
                                  "fixture", "b" * 64, 2, "a" * 64)

    def submit(document, content):
        object_id = secrets.token_hex(32)
        key = f"v1/tenants/{hashlib.sha256(tenant.encode('ascii')).hexdigest()}/documents/{object_id}"
        stored = client.put_object(bucket, key, io.BytesIO(content), len(content))
        assert stored.version_id
        scope = DocumentScope(tenant, "app", "collection", document, "v1", "a" * 64)
        jobs.submit(uuid4(), IndexJob(scope, object_id, hashlib.sha256(content).hexdigest(), len(content),
                                      stored.version_id, 0, ("alice",)))

    submit("clean", b"forest clean document")
    assert process_one(jobs, index, tenant, resolver=resolver, scanner=scanner, parser=Parser(),
                       embedder=Engine(), embedding_space_id="a" * 64) is IndexStatus.COMPLETED
    scope = SearchScope(tenant, "app", "collection", "alice")
    assert [hit.document_id for hit in index.search(scope, "forest")] == ["clean"]
    eicar = b"X5O!P%@AP[4" + bytes([92]) + b"PZX54(P^)7CC)7}$" + b"EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
    submit("malicious", eicar)
    assert process_one(jobs, index, tenant, resolver=resolver, scanner=scanner, parser=Parser(),
                       embedder=Engine(), embedding_space_id="a" * 64) is IndexStatus.RETRY
    assert not index.search(scope, "EICAR")
