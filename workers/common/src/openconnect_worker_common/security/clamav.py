"""Fail-closed ClamAV INSTREAM scanning over a protected local socket."""

from __future__ import annotations

import math
import socket
import struct
import time
from pathlib import Path

MAX_INPUT_BYTES = 10 * 1024 * 1024
_MAX_REPLY_BYTES = 4096
_CHUNK_BYTES = 64 * 1024


class MalwareDetectedError(RuntimeError):
    """ClamAV reported a detection; signature details are intentionally withheld."""


class MalwareScanUnavailableError(RuntimeError):
    """ClamAV could not provide a trustworthy scan result."""


class ClamAvScanner:
    """Submit admitted media bytes to a locally protected clamd Unix socket.

    Clamd's TCP protocol is unauthenticated, so this adapter intentionally
    supports only an administrator-controlled Unix socket. The deployment must
    restrict socket permissions and keep signatures current.
    """

    def __init__(self, socket_path: Path, *, timeout_seconds: float = 30) -> None:
        if not socket_path.is_absolute() or not math.isfinite(timeout_seconds) or not 0 < timeout_seconds <= 120:
            raise ValueError("invalid_scanner_configuration")
        self._socket_path = socket_path
        self._timeout = timeout_seconds

    def scan(self, content: bytes, *, timeout_seconds: float | None = None) -> None:
        """Return only for a clean scan; detections and errors fail closed."""
        if type(content) is not bytes or not content or len(content) > MAX_INPUT_BYTES:
            raise ValueError("invalid_document")
        if timeout_seconds is not None and (not math.isfinite(timeout_seconds) or timeout_seconds <= 0):
            raise ValueError("invalid_timeout")
        if not hasattr(socket, "AF_UNIX"):
            raise MalwareScanUnavailableError("malware_scan_unavailable")
        deadline = time.monotonic() + min(self._timeout, timeout_seconds or self._timeout)
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                self._set_timeout(connection, deadline)
                connection.connect(str(self._socket_path))
                self._set_timeout(connection, deadline)
                connection.sendall(b"zINSTREAM\0")
                for offset in range(0, len(content), _CHUNK_BYTES):
                    chunk = content[offset:offset + _CHUNK_BYTES]
                    self._set_timeout(connection, deadline)
                    connection.sendall(struct.pack("!I", len(chunk)) + chunk)
                self._set_timeout(connection, deadline)
                connection.sendall(struct.pack("!I", 0))
                reply = bytearray()
                while b"\0" not in reply:
                    self._set_timeout(connection, deadline)
                    data = connection.recv(min(1024, _MAX_REPLY_BYTES + 1 - len(reply)))
                    if not data:
                        raise MalwareScanUnavailableError("malware_scan_unavailable")
                    reply.extend(data)
                    if len(reply) > _MAX_REPLY_BYTES:
                        raise MalwareScanUnavailableError("malware_scan_unavailable")
                if time.monotonic() >= deadline:
                    raise TimeoutError("malware_scan_timeout")
        except (OSError, TimeoutError):
            raise MalwareScanUnavailableError("malware_scan_unavailable") from None
        if reply == b"stream: OK\0":
            return
        if (reply.startswith(b"stream: ") and reply.endswith(b" FOUND\0")
                and b"\n" not in reply and reply.count(b"\0") == 1):
            raise MalwareDetectedError("malware_detected")
        raise MalwareScanUnavailableError("malware_scan_unavailable")

    @staticmethod
    def _set_timeout(connection: socket.socket, deadline: float) -> None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("malware_scan_timeout")
        connection.settimeout(remaining)
