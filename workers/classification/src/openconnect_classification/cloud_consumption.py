"""Private worker-to-control-plane claim consumption; no retry after ambiguity."""

from __future__ import annotations

import http.client
import json
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

    def consume(self, reservation_id: str, claim_id: str) -> str:
        """Return consumed, already_consumed or unavailable without retrying."""
        try:
            reservation = UUID(reservation_id)
            claim = UUID(claim_id)
            if reservation.int == 0 or claim.int == 0:
                return "unavailable"
        except (TypeError, ValueError, AttributeError):
            return "unavailable"
        connection = http.client.HTTPSConnection(
            self._host, self._port, context=self._context, timeout=self._timeout
        )
        try:
            path = f"/cloud/internal/v1/classification/quota/reservations/{reservation}/claims/{claim}/consume"
            connection.request("POST", path, body=b"", headers={"Cache-Control": "no-store"})
            response = connection.getresponse()
            if response.getheader("Content-Encoding") is not None:
                return "unavailable"
            data = response.read(4097)
            if len(data) > 4096:
                return "unavailable"
            value = json.loads(data, object_pairs_hook=_unique_object)
            if value == {"status": "consumed"} and response.status == 200:
                return "consumed"
            if value == {"status": "already_consumed"} and response.status == 409:
                return "already_consumed"
        except (OSError, http.client.HTTPException, ValueError, UnicodeError):
            pass
        finally:
            connection.close()
        return "unavailable"


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate property")
        result[key] = value
    return result
