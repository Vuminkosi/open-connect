#!/usr/bin/env python3
"""Private backend demo target behind the OWASP CRS WAF proxy (FW-060).

Runs strictly on the internal Docker network with NO published host ports.
Tracks actual backend hit counts atomically and never echoes or logs raw request payloads.
"""

from __future__ import annotations

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading

_LOCK = threading.Lock()
_HIT_COUNT = 0


def _increment_hits() -> int:
    global _HIT_COUNT
    with _LOCK:
        _HIT_COUNT += 1
        return _HIT_COUNT


def _get_hits() -> int:
    with _LOCK:
        return _HIT_COUNT


class WafDemoHandler(BaseHTTPRequestHandler):
    def log_message(self, format: str, *args: object) -> None:
        # Intentionally suppress default access logs that could include query strings.
        return

    def _send_json(self, status: int, payload: dict[str, object]) -> None:
        raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self) -> None:
        path = self.path.split("?", 1)[0]
        if path == "/api/waf-demo/health":
            self._send_json(200, {"status": "ready"})
            return
        if path == "/api/waf-demo/echo":
            hits = _increment_hits()
            self._send_json(200, {"ok": True, "route": "/api/waf-demo/echo", "hitCount": hits})
            return
        if path == "/api/waf-demo/stats":
            self._send_json(200, {"route": "/api/waf-demo/echo", "hitCount": _get_hits()})
            return
        if path == "/_blazor/negotiate":
            self._send_json(200, {"negotiateVersion": 1, "availableTransports": ["WebSockets"]})
            return
        self._send_json(404, {"error": "not_found"})

    def do_POST(self) -> None:
        path = self.path.split("?", 1)[0]
        content_length = int(self.headers.get("Content-Length", "0") or "0")
        if content_length > 4096:
            self._send_json(413, {"error": "payload_too_large"})
            return
        if content_length > 0:
            # Drain bounded request body without logging or echoing it.
            self.rfile.read(content_length)
        if path == "/api/waf-demo/echo":
            hits = _increment_hits()
            self._send_json(200, {"ok": True, "route": "/api/waf-demo/echo", "hitCount": hits})
            return
        if path == "/_blazor/negotiate":
            self._send_json(200, {"negotiateVersion": 1, "availableTransports": ["WebSockets"]})
            return
        self._send_json(404, {"error": "not_found"})


if __name__ == "__main__":
    server = ThreadingHTTPServer(("0.0.0.0", 8081), WafDemoHandler)
    server.serve_forever()
