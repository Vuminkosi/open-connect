"""Deployment-certificate-bound control-plane calls for LiveUI workers."""

import base64
import hashlib
import hmac
import http.client
import json
import re
import ssl
from pathlib import Path
from urllib.parse import urlsplit
from uuid import UUID


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate property")
        result[key] = value
    return result


class CloudLiveUIClient:
    def __init__(self, origin: str, ca: Path, cert: Path, key: Path, timeout: float = 5):
        parsed = urlsplit(origin)
        port = parsed.port
        if (
            parsed.scheme != "https"
            or not parsed.hostname
            or parsed.username is not None
            or parsed.password is not None
            or parsed.path not in ("", "/")
            or parsed.query
            or parsed.fragment
            or port == 0
            or not all(path.is_absolute() for path in (ca, cert, key))
            or isinstance(timeout, bool)
            or not isinstance(timeout, (int, float))
            or not 0 < timeout <= 30
        ):
            raise ValueError("invalid control-plane configuration")
        self._host = parsed.hostname
        self._port = port or 443
        self._timeout = timeout
        self._context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        self._context.load_verify_locations(cafile=str(ca))
        self._context.load_cert_chain(certfile=str(cert), keyfile=str(key))

    def _call(
        self, method: str, path: str, headers: dict[str, str] | None = None, body: bytes | None = None
    ) -> tuple[int, object] | None:
        connection = http.client.HTTPSConnection(self._host, self._port, context=self._context, timeout=self._timeout)
        try:
            connection.request(
                method,
                path,
                body=(body if body is not None else b"") if method == "POST" else None,
                headers={"Cache-Control": "no-store", **(headers or {})},
            )
            response = connection.getresponse()
            if response.getheader("Content-Encoding") is not None:
                return None
            data = response.read(4097)
            if len(data) > 4096:
                return None
            return response.status, json.loads(data, object_pairs_hook=_unique)
        except (OSError, http.client.HTTPException, ValueError, UnicodeError):
            return None
        finally:
            connection.close()

    def ready_deployment(self) -> str | None:
        result = self._call("GET", "/cloud/internal/v1/worker/ready")
        try:
            if (
                result is None
                or result[0] != 200
                or not isinstance(result[1], dict)
                or set(result[1]) != {"status", "deploymentId"}
            ):
                return None
            value = result[1]
            deployment = UUID(value["deploymentId"])
            return str(deployment) if value["status"] == "ready" and deployment.int != 0 else None
        except (ValueError, TypeError, AttributeError):
            return None

    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> dict | None:
        return self._consume(reservation_id, claim_id, payload, key, None)

    def consume_metered(
        self, reservation_id: str, claim_id: str, payload: bytes, key: bytes, context_tokens: int
    ) -> dict | None:
        if type(context_tokens) is not int or not 256 <= context_tokens <= 32768:
            return None
        return self._consume(reservation_id, claim_id, payload, key, context_tokens)

    def _consume(
        self, reservation_id: str, claim_id: str, payload: bytes, key: bytes, context_tokens: int | None
    ) -> dict | None:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if (
                reservation.int == 0
                or claim.int == 0
                or not isinstance(payload, bytes)
                or not 0 < len(payload) <= 131072
                or not isinstance(key, bytes)
                or len(key) != 32
            ):
                return None
            digest = base64.b64encode(hmac.digest(key, payload, hashlib.sha256)).decode("ascii")
            suffix = "consume-metered" if context_tokens is not None else "consume"
            headers = {"X-OpenConnect-Worker-Payload-Fingerprint": digest}
            if context_tokens is not None:
                headers["X-OpenConnect-Worker-Context-Tokens"] = str(context_tokens)
            result = self._call(
                "POST",
                f"/cloud/internal/v1/liveui/quota/reservations/{reservation}/claims/{claim}/{suffix}",
                headers,
            )
            if result is None or result[0] != 200 or not isinstance(result[1], dict):
                return None
            value = result[1]
            if (
                set(value) != {"status", "modelId", "modelRevision", "modelDigest"}
                or value["status"] != "consumed"
                or not isinstance(value["modelId"], str)
                or not 0 < len(value["modelId"]) <= 256
                or not isinstance(value["modelRevision"], str)
                or not 0 < len(value["modelRevision"]) <= 256
                or not isinstance(value["modelDigest"], str)
                or not re.fullmatch(r"sha256:[0-9a-f]{64}", value["modelDigest"])
            ):
                return None
            return value
        except (ValueError, TypeError, AttributeError):
            return None

    def settle(self, reservation_id: str, claim_id: str) -> bool:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if reservation.int == 0 or claim.int == 0:
                return False
            result = self._call(
                "POST", f"/cloud/internal/v1/liveui/quota/reservations/{reservation}/claims/{claim}/complete"
            )
            return (
                result is not None
                and result[0] == 200
                and result[1] in ({"status": "completed"}, {"status": "already_completed"})
            )
        except (ValueError, TypeError, AttributeError):
            return False

    def settle_metered(self, reservation_id: str, claim_id: str, input_tokens: int, output_tokens: int) -> bool:
        return self._settle_metered(reservation_id, claim_id, input_tokens, output_tokens, "complete")

    def fail_metered(self, reservation_id: str, claim_id: str, input_tokens: int, output_tokens: int) -> bool:
        return self._settle_metered(reservation_id, claim_id, input_tokens, output_tokens, "fail")

    def observe_metered(self, reservation_id: str, claim_id: str, input_tokens: int, output_tokens: int) -> bool:
        return self._settle_metered(reservation_id, claim_id, input_tokens, output_tokens, "observe")

    def _settle_metered(
        self, reservation_id: str, claim_id: str, input_tokens: int, output_tokens: int, operation: str
    ) -> bool:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if (
                reservation.int == 0 or claim.int == 0
                or type(input_tokens) is not int or type(output_tokens) is not int
                or input_tokens < 0 or output_tokens < 0 or input_tokens + output_tokens > 32768
            ):
                return False
            body = json.dumps(
                {"inputTokens": input_tokens, "outputTokens": output_tokens}, separators=(",", ":")
            ).encode()
            suffix = operation + "-metered"
            result = self._call(
                "POST", f"/cloud/internal/v1/liveui/quota/reservations/{reservation}/claims/{claim}/{suffix}",
                {"Content-Type": "application/json"}, body,
            )
            statuses = {
                "complete": ({"status": "completed"}, {"status": "already_completed"}),
                "fail": ({"status": "failed"}, {"status": "already_failed"}),
                "observe": ({"status": "observed"}, {"status": "already_observed"}),
            }[operation]
            return result is not None and result[0] == 200 and result[1] in statuses
        except (ValueError, TypeError, AttributeError):
            return False
