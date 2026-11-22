"""Single-use Cloud claim consumption over deployment-authenticated TLS."""

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


class CloudClaimConsumer:
    """Consume a claim once without retrying an ambiguous operation."""

    def __init__(self, origin: str, ca_file: Path, cert_file: Path, key_file: Path, timeout: float = 5,
                 *, capability: str):
        parsed = urlsplit(origin)
        try:
            port = parsed.port
        except ValueError as exc:
            raise ValueError("invalid cloud authority") from exc
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
                or parsed.password is not None or parsed.path not in ("", "/") or parsed.query
                or parsed.fragment or port == 0
                or not all(path.is_absolute() for path in (ca_file, cert_file, key_file))
                or isinstance(timeout, bool) or not isinstance(timeout, (int, float))
                or not 0 < timeout <= 30 or capability not in ("analytics", "knowledge-embed")):
            raise ValueError("invalid cloud authority configuration")
        self._host = parsed.hostname
        self._port = port or 443
        self._timeout = timeout
        self._capability = capability
        self._context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        self._context.load_verify_locations(cafile=str(ca_file))
        self._context.load_cert_chain(certfile=str(cert_file), keyfile=str(key_file))

    def ready_deployment(self) -> str | None:
        """Confirm the authority recognizes this worker certificate."""
        connection = http.client.HTTPSConnection(
            self._host, self._port, context=self._context, timeout=self._timeout
        )
        try:
            connection.request("GET", "/cloud/internal/v1/worker/ready", headers={"Cache-Control": "no-store"})
            response = connection.getresponse()
            if response.status != 200 or response.getheader("Content-Encoding") is not None:
                return None
            body = response.read(4097)
            if len(body) > 4096:
                return None
            value = json.loads(body, object_pairs_hook=_unique)
            if not isinstance(value, dict) or set(value) != {"status", "deploymentId"} or value["status"] != "ready":
                return None
            deployment = UUID(value["deploymentId"])
            return str(deployment) if deployment.int else None
        except (OSError, http.client.HTTPException, ValueError, UnicodeError, TypeError, AttributeError, RecursionError):
            return None
        finally:
            connection.close()

    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> str:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if (reservation.int == 0 or claim.int == 0 or not isinstance(payload, bytes)
                    or not 0 < len(payload) <= (256 if self._capability == "analytics" else 128) * 1024
                    or not isinstance(key, bytes) or len(key) != 32):
                return "unavailable"
        except (TypeError, ValueError, AttributeError):
            return "unavailable"
        connection = http.client.HTTPSConnection(
            self._host, self._port, context=self._context, timeout=self._timeout
        )
        try:
            path = f"/cloud/internal/v1/{self._capability}/quota/reservations/{reservation}/claims/{claim}/consume"
            digest = hmac.digest(key, payload, hashlib.sha256)
            connection.request("POST", path, body=b"", headers={
                "Cache-Control": "no-store",
                "X-OpenConnect-Worker-Payload-Fingerprint": base64.b64encode(digest).decode("ascii"),
            })
            response = connection.getresponse()
            if response.getheader("Content-Encoding") is not None:
                return "unavailable"
            body = response.read(4097)
            if len(body) > 4096:
                return "unavailable"
            value = json.loads(body, object_pairs_hook=_unique)
            if value == {"status": "consumed"} and response.status == 200:
                return "consumed"
            if value == {"status": "already_consumed"} and response.status == 409:
                return "already_consumed"
        except (OSError, http.client.HTTPException, ValueError, UnicodeError, RecursionError):
            pass
        finally:
            connection.close()
        return "unavailable"
