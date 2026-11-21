"""Private knowledge embedding endpoint; model loading is startup-only."""

from __future__ import annotations

import asyncio
import os
import re
import threading
from collections.abc import Iterable
from contextlib import asynccontextmanager
from dataclasses import asdict
from datetime import datetime, timedelta, timezone
from functools import lru_cache
from pathlib import Path
from typing import Annotated, Literal
from uuid import UUID

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, StrictFloat, StrictInt, ValidationError, field_validator

from openconnect_knowledge.embeddings import EmbeddingProfile, OnnxEmbeddingEngine
from openconnect_knowledge.reranker import Candidate, OnnxReranker
from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.index_jobs import IndexJob, IndexWorkerUnavailable, PostgresIndexJobs
from openconnect_knowledge.image_index import PostgresImageIndex
from openconnect_knowledge.postgres_index import IndexConflictError, PostgresKnowledgeIndex, SearchScope
from openconnect_knowledge.search import search_and_rerank
from openconnect_worker_common.contracts.capability import (
    CapabilityDescriptor, CapabilityKind, CapabilityState,
)
from openconnect_worker_common.contracts.strict_json import parse_signed_json
from openconnect_worker_common.models.resource_admission import (
    AdmissionVerdict, ResourceBudget, ResourceRequest, admit,
)
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 128 * 1024
_slot = threading.BoundedSemaphore(1)


class Profile(BaseModel):
    model_config = ConfigDict(extra="forbid")
    modelAsset: str
    tokenizerAsset: str
    dimension: int = Field(ge=1, le=8192)
    maxTokens: int = Field(ge=2, le=512)
    prefixStyle: Literal["none", "e5"] = "none"
    padToken: str = "[PAD]"


class ServiceConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")
    manifest: RuntimeModelManifest
    profile: Profile
    resourceRequest: ResourceRequest
    resourceBudget: ResourceBudget
    licenseAccepted: bool = False
    commercialUse: bool = False


class RerankProfile(BaseModel):
    model_config = ConfigDict(extra="forbid")
    modelAsset: str
    tokenizerAsset: str
    maxTokens: int = Field(ge=2, le=512)
    padToken: str = "[PAD]"


class RerankServiceConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")
    manifest: RuntimeModelManifest
    profile: RerankProfile
    resourceRequest: ResourceRequest
    resourceBudget: ResourceBudget
    licenseAccepted: bool = False
    commercialUse: bool = False


class _PrivateModel(BaseModel):
    """Keep request data out of accidental diagnostic representations."""

    def __repr_args__(self) -> Iterable[tuple[str | None, object]]:
        return iter(())


class Payload(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    inputKind: Literal["query", "passage"]
    texts: list[Annotated[str, Field(min_length=1, max_length=8192)]] = Field(
        min_length=1, max_length=32
    )


class ExecuteRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.embed"]
    tenantId: str = Field(min_length=1, max_length=128)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: Payload


class RerankCandidate(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(min_length=1, max_length=256)
    text: str = Field(min_length=1, max_length=8192)


class RerankPayload(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    query: str = Field(min_length=1, max_length=8192)
    candidates: list[RerankCandidate] = Field(min_length=1, max_length=32)


class RerankRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.rerank"]
    tenantId: str = Field(min_length=1, max_length=128)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: RerankPayload


class HybridQuery(_PrivateModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    vector: list[StrictFloat] = Field(min_length=1, max_length=8192)
    embeddingSpaceId: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")
    modelId: str = Field(min_length=1, max_length=256)
    fingerprint: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")


class SearchPayload(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    applicationId: str = Field(min_length=1, max_length=256)
    collectionId: str = Field(min_length=1, max_length=256)
    principalId: str = Field(min_length=1, max_length=256)
    roleIds: list[Annotated[str, Field(min_length=1, max_length=256)]] = Field(default_factory=list, max_length=64)
    query: str = Field(min_length=1, max_length=8192)
    limit: StrictInt = Field(default=10, ge=1, le=32)
    hybrid: HybridQuery | None = None


class SearchRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.search"]
    tenantId: str = Field(min_length=1, max_length=128)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: SearchPayload


class IndexPayload(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    jobId: UUID
    applicationId: str = Field(min_length=1, max_length=256)
    collectionId: str = Field(min_length=1, max_length=256)
    documentId: str = Field(min_length=1, max_length=256)
    sourceRevision: str = Field(min_length=1, max_length=256)
    embeddingSpaceId: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")
    objectId: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")
    objectSha256: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")
    objectSize: StrictInt = Field(ge=1, le=10 * 1024 * 1024)
    objectVersion: str = Field(min_length=1, max_length=1024)
    expectedGeneration: StrictInt = Field(ge=0, lt=2**63 - 1)
    principals: list[Annotated[str, Field(min_length=1, max_length=256)]] = Field(default_factory=list, max_length=64)
    roles: list[Annotated[str, Field(min_length=1, max_length=256)]] = Field(default_factory=list, max_length=64)
    documentExpiresAt: AwareDatetime | None = None

    @field_validator("objectVersion")
    @classmethod
    def immutable_object_version(cls, value: str) -> str:
        if value == "null" or any(ord(char) < 32 or ord(char) == 127 for char in value):
            raise ValueError("invalid_object_version")
        try:
            value.encode("utf-8")
        except UnicodeEncodeError:
            raise ValueError("invalid_object_version") from None
        return value


class IndexRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.index"]
    tenantId: str = Field(min_length=26, max_length=26, pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: IndexPayload


class IndexStatusPayload(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    jobId: UUID
    applicationId: str = Field(min_length=1, max_length=256)
    collectionId: str = Field(min_length=1, max_length=256)
    documentId: str = Field(min_length=1, max_length=256)
    principalId: str = Field(min_length=1, max_length=256)
    roleIds: list[Annotated[str, Field(min_length=1, max_length=256)]] = Field(default_factory=list, max_length=64)


class IndexStatusRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.index.status"]
    tenantId: str = Field(min_length=26, max_length=26, pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: IndexStatusPayload


class IndexDeletePayload(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    applicationId: str = Field(min_length=1, max_length=256)
    collectionId: str = Field(min_length=1, max_length=256)
    documentId: str = Field(min_length=1, max_length=256)
    expectedGeneration: StrictInt = Field(ge=0, lt=2**63 - 1)


class IndexDeleteRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.index.delete"]
    tenantId: str = Field(min_length=26, max_length=26, pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: IndexDeletePayload


class ImageVector(_PrivateModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    vector: list[StrictFloat] = Field(min_length=1, max_length=8192)
    embeddingSpaceId: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")
    modelId: str = Field(min_length=1, max_length=256)
    fingerprint: str = Field(min_length=64, max_length=64, pattern=r"^[0-9a-f]{64}$")


class ImageUpsertPayload(ImageVector):
    applicationId: str = Field(min_length=1, max_length=256)
    collectionId: str = Field(min_length=1, max_length=256)
    documentId: str = Field(min_length=1, max_length=256)
    sourceRevision: str = Field(min_length=1, max_length=256)
    expectedGeneration: StrictInt = Field(ge=1, lt=2**63 - 1)
    imageId: str = Field(min_length=1, max_length=256)
    imageRevision: str = Field(min_length=1, max_length=256)
    expectedImageRevision: str | None = Field(default=None, min_length=1, max_length=256)


class ImageSearchPayload(ImageVector):
    applicationId: str = Field(min_length=1, max_length=256)
    collectionId: str = Field(min_length=1, max_length=256)
    principalId: str = Field(min_length=1, max_length=256)
    roleIds: list[Annotated[str, Field(min_length=1, max_length=256)]] = Field(default_factory=list, max_length=64)
    limit: StrictInt = Field(default=10, ge=1, le=100)


class ImageRequest(_PrivateModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.image.upsert", "knowledge.image.search"]
    tenantId: str = Field(min_length=26, max_length=26, pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: ImageUpsertPayload | ImageSearchPayload


def _load_engine() -> OnnxEmbeddingEngine | None:
    if os.environ.get("OC_KNOWLEDGE_ENABLED", "false").lower() != "true":
        return None
    if os.environ.get("OC_MODEL_PROVISIONING", "first-start-if-missing") == "disabled":
        return None
    if os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real":
        return None
    if os.environ.get("OC_MODEL_DEVICE", "auto").lower() not in {"auto", "cpu"}:
        return None
    try:
        path = Path(os.environ["OC_KNOWLEDGE_CONFIG_PATH"])
        if path.stat().st_size > 1024 * 1024:
            return None
        config = ServiceConfig.model_validate_json(path.read_bytes())
        if config.manifest.workerKind != "knowledge":
            return None
        requested = config.resourceRequest
        if (requested.memoryMb <= 0 or requested.cpuMillicores <= 0 or requested.gpuRequired
                or requested.vramMb > 0
                or admit(requested, config.resourceBudget).verdict != AdmissionVerdict.ADMIT):
            return None
        profile = config.profile
        engine = OnnxEmbeddingEngine(
            RuntimeModelCacheManager(Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")), "knowledge"),
            config.manifest, EmbeddingProfile(profile.modelAsset, profile.tokenizerAsset,
                                             profile.dimension, profile.maxTokens,
                                             profile.prefixStyle, profile.padToken),
            license_accepted=config.licenseAccepted, commercial_use=config.commercialUse,
        )
        engine.embed(["startup readiness probe"], input_kind="query")
        return engine
    except Exception:
        # Startup errors may contain paths or model internals; never expose them.
        return None


def _load_reranker() -> OnnxReranker | None:
    if (os.environ.get("OC_RERANK_ENABLED", "false").lower() != "true"
            or os.environ.get("OC_MODEL_PROVISIONING", "first-start-if-missing") == "disabled"
            or os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real"
            or os.environ.get("OC_MODEL_DEVICE", "auto").lower() not in {"auto", "cpu"}):
        return None
    try:
        path = Path(os.environ["OC_RERANK_CONFIG_PATH"])
        if path.stat().st_size > 1024 * 1024:
            return None
        config = RerankServiceConfig.model_validate_json(path.read_bytes())
        requested = config.resourceRequest
        if (config.manifest.workerKind != "knowledge" or requested.memoryMb <= 0
                or requested.cpuMillicores <= 0 or requested.gpuRequired or requested.vramMb > 0
                or admit(requested, config.resourceBudget).verdict != AdmissionVerdict.ADMIT):
            return None
        profile = config.profile
        engine = OnnxReranker(RuntimeModelCacheManager(Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")),
                                                       "knowledge"), config.manifest,
            model_asset=profile.modelAsset, tokenizer_asset=profile.tokenizerAsset,
            max_tokens=profile.maxTokens, pad_token=profile.padToken,
            license_accepted=config.licenseAccepted, commercial_use=config.commercialUse)
        engine.rank("startup readiness", [Candidate("probe", "startup readiness")])
        return engine
    except Exception:
        return None


def _load_index() -> PostgresKnowledgeIndex | None:
    if (os.environ.get("OC_KNOWLEDGE_SEARCH_ENABLED", "false").lower() != "true"
            and os.environ.get("OC_INDEX_DELETE_ENABLED", "false").lower() != "true"):
        return None
    try:
        return PostgresKnowledgeIndex(os.environ["OC_KNOWLEDGE_SEARCH_DSN"])
    except Exception:
        return None


def _load_jobs() -> PostgresIndexJobs | None:
    if os.environ.get("OC_INDEX_SUBMISSION_ENABLED", "false").lower() != "true":
        return None
    try:
        return PostgresIndexJobs(os.environ["OC_KNOWLEDGE_SEARCH_DSN"])
    except Exception:
        return None


def _load_image_index() -> PostgresImageIndex | None:
    if os.environ.get("OC_KNOWLEDGE_IMAGE_ENABLED", "false").lower() != "true":
        return None
    try:
        return PostgresImageIndex(os.environ["OC_KNOWLEDGE_SEARCH_DSN"])
    except Exception:
        return None


@asynccontextmanager
async def lifespan(app: FastAPI):
    app.state.engine = await run_in_threadpool(_load_engine)
    app.state.reranker = await run_in_threadpool(_load_reranker)
    app.state.index = await run_in_threadpool(_load_index)
    app.state.jobs = await run_in_threadpool(_load_jobs)
    app.state.image_index = await run_in_threadpool(_load_image_index)
    yield
    app.state.engine = None
    app.state.reranker = None
    app.state.index = None
    app.state.jobs = None
    app.state.image_index = None


app = FastAPI(title="OpenConnect Knowledge Worker", docs_url=None, redoc_url=None,
              openapi_url=None, lifespan=lifespan)


def _configured() -> bool:
    key = key_from_environment()
    return bool(os.environ.get("OPENCONNECT_WORKER_TOKEN")) and bool(key and len(key) >= 32)


def _engine_ready(engine) -> bool:
    if not _configured() or engine is None:
        return False
    try:
        engine._verify()
        return True
    except Exception:
        return False


def _ready() -> bool:
    return (any(_engine_ready(getattr(app.state, name, None)) for name in ("engine", "reranker", "image_index"))
            or _jobs_ready() or _delete_ready())


def _delete_ready() -> bool:
    return (os.environ.get("OC_INDEX_DELETE_ENABLED", "false").lower() == "true"
            and _engine_ready(getattr(app.state, "index", None)))


def _jobs_ready() -> bool:
    jobs = getattr(app.state, "jobs", None)
    if not _configured() or jobs is None:
        return False
    try:
        return bool(jobs.live_worker_spaces())
    except Exception:
        return False

def _error(code: str, status: int) -> JSONResponse:
    return JSONResponse({"error": {"code": code}}, status_code=status)


def _authorized(request: Request) -> bool:
    try:
        return verify_bearer(request.headers.get("authorization"), os.environ.get("OPENCONNECT_WORKER_TOKEN"))
    except TypeError:
        return False


@lru_cache(maxsize=1)
def _cloud_consumer():
    if os.environ.get("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "true":
        return None
    from openconnect_knowledge.cloud_consumption import KnowledgeEmbeddingClaimConsumer

    names = ("OC_KNOWLEDGE_CLOUD_AUTHORITY_ORIGIN", "OC_KNOWLEDGE_CLOUD_CA_FILE",
             "OC_KNOWLEDGE_CLOUD_CERT_FILE", "OC_KNOWLEDGE_CLOUD_KEY_FILE")
    values = [os.environ.get(name) for name in names]
    if not all(values):
        raise ValueError("incomplete_cloud_configuration")
    origin, ca, certificate, key = values
    return KnowledgeEmbeddingClaimConsumer(origin, Path(ca), Path(certificate), Path(key))


class _Busy(Exception):
    pass


def _before_expiry(expires_at, operation, *args, **kwargs):
    """Do not start a queued database operation after its signed request expires."""
    if datetime.now(timezone.utc) >= expires_at:
        raise ValueError("expired_request")
    return operation(*args, **kwargs)


def _execute_model(engine, command):
    # The inference thread owns the slot until native inference actually ends.
    if not _slot.acquire(blocking=False):
        raise _Busy()
    try:
        if datetime.now(timezone.utc) >= command.expiresAt:
            raise ValueError("expired_request")
        if command.kind == "knowledge.embed":
            return engine.embed(command.payload.texts, input_kind=command.payload.inputKind)
        if command.kind == "knowledge.search":
            payload = command.payload
            scope = SearchScope(command.tenantId, payload.applicationId, payload.collectionId,
                                payload.principalId, tuple(payload.roleIds))
            hybrid = payload.hybrid
            return search_and_rerank(app.state.index, engine, scope, payload.query, limit=payload.limit,
                vector=hybrid.vector if hybrid else None,
                embedding_space_id=hybrid.embeddingSpaceId if hybrid else None,
                model_id=hybrid.modelId if hybrid else None,
                fingerprint=hybrid.fingerprint if hybrid else None)
        return engine.rank(command.payload.query, [Candidate(item.id, item.text) for item in command.payload.candidates])
    finally:
        _slot.release()


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> JSONResponse:
    ready_now = _ready()
    return JSONResponse({"ready": ready_now}, status_code=200 if ready_now else 503)


@app.get("/internal/v1/capabilities")
def capabilities(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authorized(request):
        return _error("unauthorized", 401)
    descriptors = []
    for state_name, capability_id, kind in (("engine", "knowledge.embed", CapabilityKind.EMBEDDING),
                                            ("reranker", "knowledge.rerank", CapabilityKind.TOOL)):
        if _engine_ready(getattr(app.state, state_name, None)):
            descriptors.append(CapabilityDescriptor(id=capability_id, kind=kind, state=CapabilityState.READY,
                                                    version="1.0.0", localOnly=True).model_dump(mode="json"))
    if (os.environ.get("OC_KNOWLEDGE_SEARCH_ENABLED", "false").lower() == "true"
            and _engine_ready(getattr(app.state, "reranker", None)) and _engine_ready(getattr(app.state, "index", None))):
        descriptors.append(CapabilityDescriptor(id="knowledge.search", kind=CapabilityKind.TOOL,
            state=CapabilityState.READY, version="1.0.0", localOnly=True).model_dump(mode="json"))
    if _jobs_ready():
        descriptors.append(CapabilityDescriptor(id="knowledge.index", kind=CapabilityKind.TOOL,
            state=CapabilityState.READY, version="1.0.0", localOnly=True).model_dump(mode="json"))
    if _delete_ready():
        descriptors.append(CapabilityDescriptor(id="knowledge.index.delete", kind=CapabilityKind.TOOL,
            state=CapabilityState.READY, version="1.0.0", localOnly=True).model_dump(mode="json"))
    if _engine_ready(getattr(app.state, "image_index", None)):
        for capability_id in ("knowledge.image.upsert", "knowledge.image.search"):
            descriptors.append(CapabilityDescriptor(id=capability_id, kind=CapabilityKind.TOOL,
                state=CapabilityState.READY, version="1.0.0", localOnly=True).model_dump(mode="json"))
    return JSONResponse({"capabilities": descriptors})


@app.post("/internal/cloud/v1/knowledge-embed/execute")
async def execute_cloud_embedding(request: Request) -> JSONResponse:
    """Consume a Cloud claim before running the private embedding engine."""
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authorized(request):
        return _error("unauthorized", 401)
    if os.environ.get("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "true":
        return _error("capability_unavailable", 503)
    engine = getattr(app.state, "engine", None)
    if not _engine_ready(engine):
        return _error("capability_unavailable", 503)
    try:
        consumer = _cloud_consumer()
    except Exception:
        return _error("capability_unavailable", 503)
    if consumer is None:
        return _error("capability_unavailable", 503)
    body = bytearray()
    try:
        async with asyncio.timeout(10):
            async for chunk in request.stream():
                if len(body) + len(chunk) > 176000:
                    return _error("invalid_request", 413)
                body.extend(chunk)
        from openconnect_knowledge.cloud_execution import execute_claimed

        command = await run_in_threadpool(execute_claimed, bytes(body), consumer, lambda value: value)
    except TimeoutError:
        return _error("invalid_request", 408)
    finally:
        body[:] = bytes(len(body))
    if command is None:
        return _error("capability_unavailable", 503)
    try:
        remaining = (command.expiresAt - datetime.now(timezone.utc)).total_seconds()
        async with asyncio.timeout(remaining):
            result = await run_in_threadpool(_execute_model, engine, command)
    except _Busy:
        return _error("rate_limited", 429)
    except (ValueError, TimeoutError):
        return _error("invalid_request", 422)
    except Exception:
        return _error("capability_unavailable", 503)
    if datetime.now(timezone.utc) >= command.expiresAt:
        return _error("invalid_request", 422)
    return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                         "kind": command.kind, "result": asdict(result)}, headers={"Cache-Control": "no-store"})


@app.get("/internal/cloud/v1/knowledge-embed/ready")
async def cloud_embedding_ready(request: Request) -> JSONResponse:
    if not _authorized(request):
        return _error("unauthorized", 401)
    if (not _configured() or os.environ.get("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "true"
            or not _engine_ready(getattr(app.state, "engine", None))):
        return _error("capability_unavailable", 503)
    try:
        consumer = _cloud_consumer()
        deployment = await run_in_threadpool(consumer.ready_deployment) if consumer is not None else None
    except Exception:
        deployment = None
    if deployment is None:
        return _error("capability_unavailable", 503)
    return JSONResponse({"ready": True, "deploymentId": deployment}, headers={"Cache-Control": "no-store"})


@app.post("/internal/v1/execute")
async def execute(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authorized(request):
        return _error("unauthorized", 401)
    body = bytearray()
    try:
        async with asyncio.timeout(10):
            async for chunk in request.stream():
                if len(body) + len(chunk) > MAX_BODY_BYTES:
                    return _error("invalid_request", 413)
                body.extend(chunk)
    except TimeoutError:
        return _error("invalid_request", 408)
    signature = request.headers.get("x-openconnect-signature", "")
    key = key_from_environment()
    if not key or not re.fullmatch(r"[0-9a-f]{64}", signature) or not verify_bytes(bytes(body), signature, key):
        return _error("unauthorized", 401)
    try:
        parsed = parse_signed_json(body)
        kind = parsed.get("kind") if isinstance(parsed, dict) else None
        if not isinstance(kind, str):
            return _error("invalid_request", 422)
        request_type = {"knowledge.embed": ExecuteRequest, "knowledge.rerank": RerankRequest,
                        "knowledge.search": SearchRequest, "knowledge.index": IndexRequest,
                        "knowledge.index.status": IndexStatusRequest,
                        "knowledge.index.delete": IndexDeleteRequest,
                        "knowledge.image.upsert": ImageRequest, "knowledge.image.search": ImageRequest}.get(kind)
        if request_type is None:
            return _error("invalid_request", 422)
        command = request_type.model_validate(parsed)
        now = datetime.now(timezone.utc)
        if (command.issuedAt > now or command.expiresAt <= now
                or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > timedelta(minutes=5)):
            return _error("invalid_request", 422)
    except (ValidationError, ValueError, RecursionError):
        return _error("invalid_request", 422)
    if command.kind in {"knowledge.image.upsert", "knowledge.image.search"}:
        index = getattr(app.state, "image_index", None)
        if index is None or os.environ.get("OC_KNOWLEDGE_IMAGE_ENABLED", "false").lower() != "true":
            return _error("capability_unavailable", 503)
        item = command.payload
        try:
            if command.kind == "knowledge.image.upsert" and isinstance(item, ImageUpsertPayload):
                scope = DocumentScope(command.tenantId, item.applicationId, item.collectionId, item.documentId,
                                      item.sourceRevision, "0" * 64)
                await run_in_threadpool(_before_expiry, command.expiresAt, index.upsert, scope, expected_generation=item.expectedGeneration,
                    image_id=item.imageId, image_revision=item.imageRevision, vector=item.vector,
                    embedding_space_id=item.embeddingSpaceId, model_id=item.modelId, fingerprint=item.fingerprint,
                    expected_image_revision=item.expectedImageRevision)
                result = {"stored": True}
            elif command.kind == "knowledge.image.search" and isinstance(item, ImageSearchPayload):
                scope = SearchScope(command.tenantId, item.applicationId, item.collectionId,
                                    item.principalId, tuple(item.roleIds))
                hits = await run_in_threadpool(_before_expiry, command.expiresAt, index.search, scope, item.vector, embedding_space_id=item.embeddingSpaceId,
                                               model_id=item.modelId, fingerprint=item.fingerprint, limit=item.limit)
                result = {"hits": [asdict(hit) for hit in hits]}
            else:
                return _error("invalid_request", 422)
        except IndexConflictError:
            return _error("conflict", 409)
        except ValueError:
            return _error("invalid_request", 422)
        except Exception:
            return _error("capability_unavailable", 503)
        return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                             "kind": command.kind, "result": result})
    if command.kind == "knowledge.index.delete":
        index = getattr(app.state, "index", None)
        if index is None or os.environ.get("OC_INDEX_DELETE_ENABLED", "false").lower() != "true":
            return _error("capability_unavailable", 503)
        item = command.payload
        scope = DocumentScope(command.tenantId, item.applicationId, item.collectionId, item.documentId,
                              "tombstone", "0" * 64)
        try:
            generation = await run_in_threadpool(_before_expiry, command.expiresAt, index.delete, scope, expected_generation=item.expectedGeneration)
            reconciliation = await run_in_threadpool(index.deletion_reconciliation, scope, generation)
        except IndexConflictError:
            return _error("conflict", 409)
        except ValueError:
            return _error("invalid_request", 422)
        except Exception:
            return _error("capability_unavailable", 503)
        return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId, "kind": command.kind,
                             "result": {"generation": generation, "reconciliation_state": reconciliation}})
    if command.kind == "knowledge.index.status":
        jobs = getattr(app.state, "jobs", None)
        if jobs is None:
            return _error("capability_unavailable", 503)
        item = command.payload
        try:
            status = await run_in_threadpool(_before_expiry, command.expiresAt, jobs.status, command.tenantId, item.jobId,
                application_id=item.applicationId, collection_id=item.collectionId, document_id=item.documentId,
                principal_id=item.principalId, roles=tuple(item.roleIds))
        except ValueError:
            return _error("invalid_request", 422)
        except Exception:
            return _error("capability_unavailable", 503)
        if status is None:
            return _error("task_not_found", 404)
        return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                             "kind": command.kind, "result": {"state": status.state, "attempts": status.attempts,
                                 "result_generation": status.result_generation,
                                 "retry_at": status.retry_at.isoformat() if status.retry_at else None}})
    if command.kind == "knowledge.index":
        jobs = getattr(app.state, "jobs", None)
        if jobs is None:
            return _error("capability_unavailable", 503)
        item = command.payload
        scope = DocumentScope(command.tenantId, item.applicationId, item.collectionId, item.documentId,
                              item.sourceRevision, item.embeddingSpaceId)
        job = IndexJob(scope, item.objectId, item.objectSha256, item.objectSize, item.objectVersion,
                       item.expectedGeneration, tuple(item.principals), tuple(item.roles), item.documentExpiresAt)
        try:
            created = await run_in_threadpool(_before_expiry, command.expiresAt, jobs.submit, item.jobId, job, require_worker=True)
        except IndexWorkerUnavailable:
            return _error("capability_unavailable", 503)
        except ValueError as exc:
            return _error("conflict", 409) if str(exc) == "job_conflict" else _error("invalid_request", 422)
        except Exception:
            return _error("capability_unavailable", 503)
        return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                             "kind": command.kind, "result": {"job_id": str(item.jobId), "created": created}},
                            status_code=202 if created else 200)
    engine = getattr(app.state, "engine" if command.kind == "knowledge.embed" else "reranker", None)
    if (engine is None or (command.kind == "knowledge.search" and
            (getattr(app.state, "index", None) is None or
             os.environ.get("OC_KNOWLEDGE_SEARCH_ENABLED", "false").lower() != "true"))):
        return _error("capability_unavailable", 503)
    try:
        remaining = (command.expiresAt - datetime.now(timezone.utc)).total_seconds()
        async with asyncio.timeout(remaining):
            result = await run_in_threadpool(_execute_model, engine, command)
    except _Busy:
        return _error("rate_limited", 429)
    except (ValueError, TimeoutError):
        return _error("invalid_request", 422)
    except Exception:
        return _error("capability_unavailable", 503)
    if datetime.now(timezone.utc) >= command.expiresAt:
        return _error("invalid_request", 422)
    return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                         "kind": command.kind, "result": asdict(result)})
