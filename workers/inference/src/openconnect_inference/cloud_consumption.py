"""Private worker-to-control-plane claim consumption; no retry after ambiguity."""

from __future__ import annotations

import http.client
import base64
import hashlib
import hmac
import json
import re
from pathlib import Path
import ssl
from urllib.parse import urlsplit
from uuid import UUID


class CloudConsumptionClient:
    """Use deployment-owned mutual TLS credentials to consume a single claim."""

    def __init__(self, origin: str, ca_file: Path, cert_file: Path, key_file: Path, timeout: float = 5):
        parsed = urlsplit(origin)
        try:
            port = parsed.port
        except ValueError as exc:
            raise ValueError("invalid cloud control-plane origin") from exc
        if (
            parsed.scheme != "https"
            or not parsed.hostname
            or parsed.username is not None
            or parsed.password is not None
            or parsed.path not in ("", "/")
            or parsed.query
            or parsed.fragment
            or port == 0
            or not all(path.is_absolute() for path in (ca_file, cert_file, key_file))
            or isinstance(timeout, bool)
            or not isinstance(timeout, (int, float))
            or not 0 < timeout <= 30
        ):
            raise ValueError("invalid cloud control-plane configuration")
        self._host = parsed.hostname
        self._port = port or 443
        self._timeout = timeout
        self._context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        self._context.load_verify_locations(cafile=str(ca_file))
        self._context.load_cert_chain(certfile=str(cert_file), keyfile=str(key_file))

    def ready_deployment(self) -> str | None:
        """Check worker identity and control-plane readiness without mutating a claim."""
        connection = http.client.HTTPSConnection(
            self._host, self._port, context=self._context, timeout=self._timeout
        )
        try:
            connection.request("GET", "/cloud/internal/v1/worker/ready", headers={"Cache-Control": "no-store"})
            response = connection.getresponse()
            if response.status != 200 or response.getheader("Content-Encoding") is not None:
                return None
            data = response.read(4097)
            if len(data) > 4096:
                return None
            value = json.loads(data, object_pairs_hook=_unique_object)
            if not isinstance(value, dict) or set(value) != {"status", "deploymentId"} or value["status"] != "ready":
                return None
            deployment = UUID(value["deploymentId"])
            return str(deployment) if deployment.int != 0 else None
        except (OSError, http.client.HTTPException, ValueError, UnicodeError, TypeError, AttributeError):
            return None
        finally:
            connection.close()

    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> dict[str, object] | None:
        """Return the authoritative budget on first consume; never retry an ambiguous call."""
        try:
            reservation = UUID(reservation_id)
            claim = UUID(claim_id)
            if (reservation.int == 0 or claim.int == 0 or not isinstance(payload, bytes)
                    or not 0 < len(payload) <= 131072 or not isinstance(key, bytes) or len(key) != 32):
                return None
        except (TypeError, ValueError, AttributeError):
            return None
        connection = http.client.HTTPSConnection(
            self._host, self._port, context=self._context, timeout=self._timeout
        )
        try:
            path = f"/cloud/internal/v1/inference/quota/reservations/{reservation}/claims/{claim}/consume"
            digest = hmac.digest(key, payload, hashlib.sha256)
            connection.request("POST", path, body=b"", headers={
                "Cache-Control": "no-store",
                "X-OpenConnect-Worker-Payload-Fingerprint": base64.b64encode(digest).decode("ascii"),
            })
            response = connection.getresponse()
            if response.getheader("Content-Encoding") is not None:
                return None
            data = response.read(4097)
            if len(data) > 4096:
                return None
            value = json.loads(data, object_pairs_hook=_unique_object)
            if (response.status == 200 and isinstance(value, dict) and
                    set(value) == {"status", "reservedTokens", "modelId", "modelRevision", "modelDigest"} and
                    value["status"] == "consumed" and type(value["reservedTokens"]) is int and
                    0 < value["reservedTokens"] <= 9223372036854775807 and
                    isinstance(value["modelId"], str) and 0 < len(value["modelId"]) <= 256 and
                    isinstance(value["modelRevision"], str) and 0 < len(value["modelRevision"]) <= 256 and
                    isinstance(value["modelDigest"], str) and re.fullmatch(r"sha256:[0-9a-f]{64}", value["modelDigest"])):
                return value
        except (OSError, http.client.HTTPException, ValueError, UnicodeError):
            pass
        finally:
            connection.close()
        return None


    def settle(self, reservation_id: str, claim_id: str, input_tokens: int, output_tokens: int) -> bool:
        """Record terminal usage once. An ambiguous response must not be treated as settled."""
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if (reservation.int == 0 or claim.int == 0 or type(input_tokens) is not int or
                    type(output_tokens) is not int or input_tokens < 0 or output_tokens < 0 or
                    input_tokens + output_tokens > 9223372036854775807):
                return False
        except (TypeError, ValueError, AttributeError):
            return False
        connection = http.client.HTTPSConnection(self._host, self._port, context=self._context, timeout=self._timeout)
        try:
            path = f"/cloud/internal/v1/inference/quota/reservations/{reservation}/claims/{claim}/completion"
            body = json.dumps({"inputTokens": input_tokens, "outputTokens": output_tokens}, separators=(",", ":")).encode("utf-8")
            connection.request("POST", path, body=body, headers={"Cache-Control": "no-store", "Content-Type": "application/json"})
            response = connection.getresponse()
            if response.status != 200 or response.getheader("Content-Encoding") is not None:
                return False
            raw = response.read(4097)
            if len(raw) > 4096:
                return False
            value = json.loads(raw, object_pairs_hook=_unique_object)
            return value in ({"status": "completed"}, {"status": "already_completed"})
        except (OSError, http.client.HTTPException, ValueError, UnicodeError):
            return False
        finally:
            connection.close()


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate property")
        result[key] = value
    return result
