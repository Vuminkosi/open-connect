"""Tenant-bound OCR client for a protected local document-worker socket."""

from __future__ import annotations

import base64
import http.client
import json
import math
import re
import socket
import threading
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4

from pydantic import BaseModel, ConfigDict, Field, StrictFloat, StrictInt, ValidationError

from openconnect_knowledge.chunking import SourcePage
from openconnect_worker_common.security.signing import sign_bytes

_TENANT = re.compile(r"[0-7][0-9A-HJKMNP-TV-Z]{25}\Z")
_MAX_INPUT = 10 * 1024 * 1024
_MAX_OUTPUT = 8 * 1024 * 1024


class _Span(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    text: str = Field(min_length=1)
    start: StrictInt = Field(ge=0)
    end: StrictInt = Field(ge=1)
    box: list[StrictFloat] = Field(min_length=4, max_length=4)
    confidence: StrictFloat = Field(ge=0, le=1)


class _Page(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    text: str
    width: StrictInt = Field(ge=1)
    height: StrictInt = Field(ge=1)
    spans: list[_Span] = Field(max_length=50000)
    model_id: str = Field(min_length=1, max_length=1024)
    model_fingerprint: str = Field(pattern=r"^[0-9a-f]{64}$", min_length=64, max_length=64)
    language: str


class _UnixConnection(http.client.HTTPConnection):
    def __init__(self, path: Path, timeout: float):
        super().__init__("localhost", timeout=timeout)
        self._path = path

    def connect(self):
        sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        try:
            sock.settimeout(self.timeout)
            self.sock = sock
            sock.connect(str(self._path))
        except Exception:
            self.sock = None
            sock.close()
            raise


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("invalid_worker_response")
        result[key] = value
    return result


class DocumentOcrParser:
    """Send signed OCR requests only to an operator-protected Unix socket."""

    def __init__(self, socket_path: Path, token: str, key: bytes, *, timeout_seconds: float = 120):
        if (not isinstance(socket_path, Path) or not socket_path.is_absolute()
                or not isinstance(token, str) or not token or any(ord(c) < 33 or ord(c) > 126 for c in token)
                or not isinstance(key, bytes) or len(key) < 32 or isinstance(timeout_seconds, bool)
                or not isinstance(timeout_seconds, (float, int)) or not math.isfinite(timeout_seconds)
                or not 0 < timeout_seconds <= 120):
            raise ValueError("invalid_document_worker_configuration")
        self._path = socket_path
        self._token = token
        self._key = key
        self._timeout = float(timeout_seconds)

    def ready(self) -> bool:
        """Probe the local worker without sending customer content."""
        connection = _UnixConnection(self._path, min(self._timeout, 3.0))
        try:
            connection.request("GET", "/health/ready")
            return connection.getresponse().status == 200
        except Exception:
            return False
        finally:
            connection.close()

    def _post(self, body: bytes) -> bytes:
        deadline = time.monotonic() + self._timeout
        connection = _UnixConnection(self._path, self._timeout)
        expired = threading.Event()
        transport = None

        def abort() -> None:
            expired.set()
            target = transport or connection.sock
            if target is not None:
                try:
                    target.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                try:
                    target.close()
                except OSError:
                    pass

        timer = threading.Timer(self._timeout, abort)
        timer.daemon = True
        timer.start()
        try:
            connection.request("POST", "/internal/v1/execute", body, headers={
                "Authorization": "Bearer " + self._token, "X-OpenConnect-Signature": sign_bytes(body, self._key),
                "Content-Type": "application/json"})
            transport = connection.sock
            remaining = deadline - time.monotonic()
            if transport is None or remaining <= 0 or expired.is_set():
                raise TimeoutError()
            transport.settimeout(remaining)
            response = connection.getresponse()
            if response.status != 200:
                raise RuntimeError("document_worker_unavailable")
            output = bytearray()
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0 or expired.is_set():
                    raise TimeoutError()
                if transport is None:
                    raise RuntimeError("document_worker_unavailable")
                transport.settimeout(remaining)
                chunk = response.read1(min(65536, _MAX_OUTPUT + 1 - len(output)))
                if not chunk:
                    break
                output.extend(chunk)
                if len(output) > _MAX_OUTPUT:
                    raise ValueError("invalid_worker_response")
            if expired.is_set() or time.monotonic() >= deadline:
                raise TimeoutError()
            return bytes(output)
        except Exception:
            raise RuntimeError("document_worker_unavailable") from None
        finally:
            timer.cancel()
            connection.close()

    def parse(self, tenant_id: str, content: bytes) -> tuple[SourcePage, ...]:
        if (not isinstance(tenant_id, str) or not _TENANT.fullmatch(tenant_id) or type(content) is not bytes
                or not 1 <= len(content) <= _MAX_INPUT):
            raise ValueError("invalid_document")
        now = datetime.now(timezone.utc)
        task_id = uuid4().hex
        body = json.dumps({"schemaVersion": "openconnect.v1", "taskId": task_id, "kind": "documents.ocr",
                           "tenantId": tenant_id, "issuedAt": (now - timedelta(seconds=5)).isoformat(),
                           "expiresAt": (now + timedelta(seconds=self._timeout)).isoformat(),
                           "payload": {"contentBase64": base64.b64encode(content).decode("ascii")}},
                          separators=(",", ":")).encode()
        raw = self._post(body)
        try:
            result = json.loads(raw, object_pairs_hook=_unique,
                                parse_constant=lambda _: (_ for _ in ()).throw(ValueError("invalid_worker_response")))
            if (not isinstance(result, dict) or set(result) != {"schemaVersion", "taskId", "kind", "result"}
                    or result["schemaVersion"] != "openconnect.v1" or result["taskId"] != task_id
                    or result["kind"] != "documents.ocr" or not isinstance(result["result"], dict)
                    or set(result["result"]) != {"pages"} or not isinstance(result["result"]["pages"], list)
                    or not 1 <= len(result["result"]["pages"]) <= 30):
                raise ValueError("invalid_worker_response")
            pages = []
            for index, data in enumerate(result["result"]["pages"]):
                page = _Page.model_validate(data)
                if page.language != "eng":
                    raise ValueError("invalid_worker_response")
                previous_end = 0
                for i, span in enumerate(page.spans):
                    if (span.start >= span.end or span.end > len(page.text)
                            or page.text[span.start:span.end] != span.text
                            or (span.start != 0 if i == 0 else span.start != previous_end + 1
                                or page.text[previous_end:span.start] not in {" ", "\n"})
                            or any(not math.isfinite(n) or not 0 <= n <= 1 for n in span.box)
                            or span.box[0] >= span.box[2] or span.box[1] >= span.box[3]):
                        raise ValueError("invalid_worker_response")
                    previous_end = span.end
                if previous_end != len(page.text):
                    raise ValueError("invalid_worker_response")
                pages.append(SourcePage(f"page-{index + 1}", page.text))
            return tuple(pages)
        except (ValueError, TypeError, KeyError, ValidationError, UnicodeDecodeError, RecursionError):
            raise RuntimeError("invalid_worker_response") from None
