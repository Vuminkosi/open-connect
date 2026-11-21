import base64
import hashlib
import hmac
import json
import shutil
import ssl
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from uuid import uuid4

import pytest

from app.cloud_client import CloudLiveUIClient


def test_mutual_tls_client_binds_exact_payload_and_rejects_replay(tmp_path):
    if shutil.which("openssl") is None:
        pytest.skip("openssl is unavailable")
    cert, key = tmp_path / "cert.pem", tmp_path / "key.pem"
    subprocess.run(
        [
            "openssl",
            "req",
            "-x509",
            "-newkey",
            "rsa:2048",
            "-nodes",
            "-days",
            "1",
            "-subj",
            "/CN=localhost",
            "-addext",
            "subjectAltName=DNS:localhost",
            "-keyout",
            str(key),
            "-out",
            str(cert),
        ],
        check=True,
        capture_output=True,
    )
    reservation, claim, deployment = str(uuid4()), str(uuid4()), str(uuid4())
    payload, secret = b"fixture", bytes(32)
    expected = base64.b64encode(hmac.digest(secret, payload, hashlib.sha256)).decode()
    seen = []

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            assert self.connection.getpeercert()
            self.reply(200, {"status": "ready", "deploymentId": deployment})

        def do_POST(self):
            assert self.connection.getpeercert()
            seen.append(self.path)
            if self.path.endswith(("/consume", "/consume-metered")):
                assert self.headers["X-OpenConnect-Worker-Payload-Fingerprint"] == expected
                if self.path.endswith("/consume-metered"):
                    assert self.headers["X-OpenConnect-Worker-Context-Tokens"] == "2048"
                if seen.count(self.path) > 1:
                    self.reply(409, {"status": "already_consumed"})
                else:
                    self.reply(
                        200,
                        {
                            "status": "consumed",
                            "modelId": "registry/model-a",
                            "modelRevision": "r1",
                            "modelDigest": "sha256:" + "a" * 64,
                        },
                    )
            elif self.path.endswith(("/complete-metered", "/fail-metered")):
                assert self.headers["Content-Type"] == "application/json"
                assert json.loads(self.rfile.read(int(self.headers["Content-Length"]))) == {
                    "inputTokens": 410, "outputTokens": 31
                }
                self.reply(200, {"status": "failed" if self.path.endswith("/fail-metered") else "completed"})
            else:
                self.reply(200, {"status": "completed"})

        def reply(self, status, value):
            body = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_):
            pass

    server = HTTPServer(("127.0.0.1", 0), Handler)
    context = ssl.create_default_context(ssl.Purpose.CLIENT_AUTH)
    context.load_cert_chain(cert, key)
    context.load_verify_locations(cafile=str(cert))
    context.verify_mode = ssl.CERT_REQUIRED
    server.socket = context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        client = CloudLiveUIClient(f"https://localhost:{server.server_port}", cert, cert, key)
        assert client.ready_deployment() == deployment
        assert client.consume(reservation, claim, payload, secret)["modelId"] == "registry/model-a"
        assert client.consume(reservation, claim, payload, secret) is None
        assert client.settle(reservation, claim)
        assert client.consume_metered(reservation, claim, payload, secret, 2048)["modelId"] == "registry/model-a"
        assert client.consume_metered(reservation, claim, payload, secret, 2048) is None
        assert client.settle_metered(reservation, claim, 410, 31)
        assert client.fail_metered(reservation, claim, 410, 31)
        assert not client.settle_metered(reservation, claim, True, 31)
        assert not client.fail_metered(reservation, claim, -1, 31)
    finally:
        server.shutdown()
        thread.join(timeout=5)
        server.server_close()
