"""Opt-in mutual-TLS transport test against an isolated local fixture."""

import base64
import hashlib
import hmac
import http.client
import json
import os
import ssl
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from uuid import uuid4

import pytest

from openconnect_nllb.cloud_consumption import CloudConsumptionClient


@pytest.mark.skipif(not os.environ.get("OC_NLLB_TEST_TLS_DIR"), reason="Requires local test TLS certificates.")
def test_claim_client_uses_real_mutual_tls_and_payload_fingerprint():
    root = Path(os.environ["OC_NLLB_TEST_TLS_DIR"])
    deployment, reservation, claim = str(uuid4()), str(uuid4()), str(uuid4())
    events = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_GET(self):
            events.append((self.command, self.path, None))
            self.respond({"status": "ready", "deploymentId": deployment})

        def do_POST(self):
            events.append((self.command, self.path, self.headers.get("X-OpenConnect-Worker-Payload-Fingerprint")))
            self.respond({"status": "consumed" if self.path.endswith("/consume") else "completed"})

        def respond(self, body):
            payload = json.dumps(body).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

    server = HTTPServer(("127.0.0.1", 0), Handler)
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(root / "authority/authority.crt", root / "authority/authority.key")
    tls.load_verify_locations(root / "gateway/gateway.crt")
    tls.verify_mode = ssl.CERT_REQUIRED
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        client = CloudConsumptionClient(f"https://127.0.0.1:{server.server_port}", root / "gateway/authority.crt",
                                        root / "gateway/gateway.crt", root / "gateway/gateway.key")
        payload, key = b"synthetic translation payload", b"k" * 32
        assert client.ready_deployment() == deployment
        assert client.consume(reservation, claim, payload, key) == "consumed"
        assert client.complete(reservation, claim) == "completed"
        assert [event[0] for event in events] == ["GET", "POST", "POST"]
        assert events[1][2] == base64.b64encode(hmac.digest(key, payload, hashlib.sha256)).decode("ascii")
        assert events[1][1].endswith("/translation/quota/reservations/" + reservation + "/claims/" + claim + "/consume")
        untrusted = ssl.create_default_context(cafile=str(root / "gateway/authority.crt"))
        connection = http.client.HTTPSConnection("127.0.0.1", server.server_port, context=untrusted, timeout=5)
        try:
            with pytest.raises((ssl.SSLError, ConnectionResetError)):
                connection.request("GET", "/cloud/internal/v1/worker/ready")
                connection.getresponse()
        finally:
            connection.close()
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
