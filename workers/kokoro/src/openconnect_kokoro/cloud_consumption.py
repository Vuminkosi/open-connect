"""Private, deployment-bound claim consumption for Cloud synthesis."""

import base64
import hashlib
import hmac
import http.client
import json
import ssl
from pathlib import Path
from urllib.parse import urlsplit
from uuid import UUID


def unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate property")
        result[key] = value
    return result


class CloudConsumptionClient:
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

    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> str:
        try:
            reservation, claim = UUID(reservation_id), UUID(claim_id)
            if (reservation.int == 0 or claim.int == 0 or not isinstance(payload, bytes) or
                    not 0 < len(payload) <= 65536 or not isinstance(key, bytes) or len(key) != 32):
                return "unavailable"
        except (TypeError, ValueError, AttributeError):
            return "unavailable"
        connection = http.client.HTTPSConnection(self._host, self._port, context=self._context, timeout=self._timeout)
        try:
            digest = hmac.digest(key, payload, hashlib.sha256)
            path = f"/cloud/internal/v1/voice-synthesis/quota/reservations/{reservation}/claims/{claim}/consume"
            connection.request("POST", path, body=b"", headers={
                "Cache-Control": "no-store",
                "X-OpenConnect-Worker-Payload-Fingerprint": base64.b64encode(digest).decode("ascii"),
            })
            response = connection.getresponse()
            if response.getheader("Content-Encoding") is not None:
                return "unavailable"
            data = response.read(4097)
            if len(data) > 4096:
                return "unavailable"
            value = json.loads(data, object_pairs_hook=unique_object)
            if value == {"status": "consumed"} and response.status == 200:
                return "consumed"
            if value == {"status": "already_consumed"} and response.status == 409:
                return "already_consumed"
        except (OSError, http.client.HTTPException, ValueError, UnicodeError):
            pass
        finally:
            connection.close()
        return "unavailable"
