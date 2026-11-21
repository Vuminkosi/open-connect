"""Private translation claim consumption and settlement over mutual TLS."""

from __future__ import annotations

import base64
import hashlib
import hmac
import http.client
import json
import ssl
from pathlib import Path
from urllib.parse import urlsplit
from uuid import UUID


def _unique(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for name, value in pairs:
        if name in result:
            raise ValueError("duplicate property")
        result[name] = value
    return result


class CloudConsumptionClient:
    """Never retry an ambiguous consume or completion request."""

    def __init__(self, origin: str, ca: Path, cert: Path, key: Path, timeout: float = 5):
        parsed = urlsplit(origin)
        try:
            port = parsed.port
        except ValueError as exc:
            raise ValueError("invalid cloud origin") from exc
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None or
                parsed.password is not None or parsed.path not in ("", "/") or parsed.query or parsed.fragment or
                port == 0 or not all(path.is_absolute() for path in (ca, cert, key)) or
                isinstance(timeout, bool) or not isinstance(timeout, (int, float)) or not 0 < timeout <= 30):
            raise ValueError("invalid cloud consumption configuration")
        self._host, self._port, self._timeout = parsed.hostname, port or 443, timeout
        self._context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        self._context.load_verify_locations(cafile=str(ca))
        self._context.load_cert_chain(certfile=str(cert), keyfile=str(key))

    def _request(self, method: str, path: str, headers: dict[str, str]) -> tuple[int, object] | None:
        connection = None
        try:
            connection = http.client.HTTPSConnection(self._host, self._port, context=self._context, timeout=self._timeout)
            connection.request(method, path, body=b"" if method == "POST" else None, headers=headers)
            response = connection.getresponse()
            if response.getheader("Content-Encoding") is not None:
                return None
            data = response.read(4097)
            if len(data) > 4096:
                return None
            return response.status, json.loads(data, object_pairs_hook=_unique)
        except (OSError, http.client.HTTPException, ValueError, UnicodeError, RecursionError):
            return None
        finally:
            if connection is not None:
                try:
                    connection.close()
                except OSError:
                    pass

    def ready_deployment(self) -> str | None:
        result = self._request("GET", "/cloud/internal/v1/worker/ready", {"Cache-Control": "no-store"})
        if result is None or result[0] != 200 or not isinstance(result[1], dict):
            return None
        value = result[1]
        if set(value) != {"status", "deploymentId"} or value["status"] != "ready":
            return None
        try:
            deployment = UUID(value["deploymentId"])
            return str(deployment) if deployment.int else None
        except (TypeError, ValueError, AttributeError):
            return None

    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> str:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if (reservation.int == 0 or claim.int == 0 or not isinstance(payload, bytes) or
                    not 0 < len(payload) <= 65536 or not isinstance(key, bytes) or len(key) != 32):
                return "unavailable"
        except (TypeError, ValueError, AttributeError):
            return "unavailable"
        digest = hmac.digest(key, payload, hashlib.sha256)
        path = f"/cloud/internal/v1/translation/quota/reservations/{reservation}/claims/{claim}/consume"
        result = self._request("POST", path, {"Cache-Control": "no-store",
            "X-OpenConnect-Worker-Payload-Fingerprint": base64.b64encode(digest).decode("ascii")})
        if result == (200, {"status": "consumed"}):
            return "consumed"
        if result == (409, {"status": "already_consumed"}):
            return "already_consumed"
        return "unavailable"

    def complete(self, reservation_id: str, claim_id: str) -> str:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if reservation.int == 0 or claim.int == 0:
                return "unavailable"
        except (TypeError, ValueError, AttributeError):
            return "unavailable"
        path = f"/cloud/internal/v1/translation/quota/reservations/{reservation}/claims/{claim}/complete"
        result = self._request("POST", path, {"Cache-Control": "no-store"})
        if result == (200, {"status": "completed"}):
            return "completed"
        if result == (200, {"status": "already_completed"}):
            return "already_completed"
        return "unavailable"
