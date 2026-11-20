"""Opt-in disposable Docker test for the private classification TLS boundary."""

from __future__ import annotations

import http.client
import ssl
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
from uuid import uuid4

ROOT = Path(__file__).resolve().parent
IMAGE = "nginx:1.27-alpine@sha256:65645c7bb6a0661892a8b03b89d0743208a18dd2f3f17a54ef4b76fb8e2f2a10"


def run(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, capture_output=True, text=True, check=check)


def certs(root: Path, name: str, ca: str, extension: str) -> None:
    run("openssl", "req", "-newkey", "rsa:2048", "-nodes", "-keyout", str(root / f"{name}.key"),
        "-out", str(root / f"{name}.csr"), "-subj", f"/CN={name}")
    ext = root / f"{name}.ext"
    ext.write_text("basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\n" + extension, encoding="ascii")
    run("openssl", "x509", "-req", "-in", str(root / f"{name}.csr"), "-CA", str(root / f"{ca}.crt"),
        "-CAkey", str(root / f"{ca}.key"), "-CAcreateserial", "-days", "1", "-out", str(root / f"{name}.crt"), "-extfile", str(ext))


class PrivateProxyTests(unittest.TestCase):
    def test_client_auth_routes_and_body_limit(self) -> None:
        suffix = uuid4().hex[:12]
        network = "oc-classification-tls-test-" + suffix
        upstream = network + "-worker"
        proxy = network + "-proxy"
        with tempfile.TemporaryDirectory(prefix="oc-classification-tls-") as directory:
            root = Path(directory)
            for ca in ("gateway-ca", "rogue-ca"):
                run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                    "-keyout", str(root / f"{ca}.key"), "-out", str(root / f"{ca}.crt"),
                    "-subj", f"/CN={ca}", "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign")
            certs(root, "server", "gateway-ca", "subjectAltName=IP:127.0.0.1,DNS:classification-tls\nextendedKeyUsage=serverAuth\n")
            certs(root, "client", "gateway-ca", "extendedKeyUsage=clientAuth\n")
            certs(root, "rogue", "rogue-ca", "extendedKeyUsage=clientAuth\n")
            payload = root / "large.json"
            payload.write_bytes(b"x" * 180001)
            run("docker", "network", "create", network)
            try:
                code = "from http.server import BaseHTTPRequestHandler,HTTPServer;\nclass H(BaseHTTPRequestHandler):\n def do_GET(self):\n  self.send_response(200);self.end_headers();self.wfile.write((self.path+'|'+str(self.headers.get('Authorization'))).encode())\n def do_POST(self):\n  n=int(self.headers.get('Content-Length','0'));self.rfile.read(n);self.send_response(200);self.end_headers();self.wfile.write((str(n)+'|'+str(self.headers.get('Authorization'))+'|'+str(self.headers.get('Content-Type'))).encode())\nHTTPServer(('0.0.0.0',8080),H).serve_forever()"
                run("docker", "run", "-d", "--rm", "--name", upstream, "--network", network,
                    "--network-alias", "worker-classification", "python:3.12-slim", "python", "-u", "-c", code)
                config_test = run("docker", "run", "--rm", "--network", network, "--user", "101:101",
                    "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid,size=8m", "-v", str(ROOT / "nginx.conf") + ":/etc/nginx/nginx.conf:ro",
                    "-v", str(root) + ":/run/classification-tls:ro", "--entrypoint", "nginx", IMAGE, "-t", "-c", "/etc/nginx/nginx.conf", check=False)
                self.assertEqual(0, config_test.returncode, config_test.stderr)
                run("docker", "run", "-d", "--rm", "--name", proxy, "--network", network, "--user", "101:101",
                    "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges:true", "--tmpfs", "/tmp:rw,noexec,nosuid,size=8m",
                    "-p", "127.0.0.1::8443", "-v", str(ROOT / "nginx.conf") + ":/etc/nginx/nginx.conf:ro",
                    "-v", str(root) + ":/run/classification-tls:ro", "--entrypoint", "nginx", IMAGE, "-c", "/etc/nginx/nginx.conf", "-g", "daemon off;")
                port = run("docker", "port", proxy, "8443/tcp").stdout.strip().rsplit(":", 1)[1]
                def request(path: str, certificate: str | None = "client", method: str = "GET", body: bytes | None = None, headers: dict[str, str] | None = None) -> tuple[int, str]:
                    context = ssl.create_default_context(cafile=str(root / "gateway-ca.crt"))
                    if certificate:
                        context.load_cert_chain(str(root / f"{certificate}.crt"), str(root / f"{certificate}.key"))
                    connection = http.client.HTTPSConnection("127.0.0.1", int(port), context=context, timeout=5)
                    try:
                        connection.request(method, path, body=body, headers=headers or {})
                        response = connection.getresponse()
                        return response.status, response.read().decode()
                    except (OSError, http.client.HTTPException) as error:
                        return 0, str(error)
                    finally:
                        connection.close()
                status = 0
                for _ in range(50):
                    status, detail = request("/health/ready")
                    if status == 200:
                        break
                    time.sleep(0.1)
                self.assertEqual(200, status, detail)
                status, body = request("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"})
                self.assertEqual((200, "/internal/v1/capabilities|Bearer test-token"), (status, body))
                self.assertNotEqual(200, request("/health/ready", None)[0])
                self.assertNotEqual(200, request("/health/ready", "rogue")[0])
                self.assertEqual(404, request("/internal/v1/execute")[0])
                self.assertEqual((200, "2|Bearer test-token|application/json"), request("/internal/cloud/v1/classification/execute", method="POST", body=b"{}", headers={"Content-Type": "application/json", "Authorization": "Bearer test-token"}))
                self.assertEqual(405, request("/internal/cloud/v1/classification/execute")[0])
                self.assertEqual(413, request("/internal/cloud/v1/classification/execute", method="POST", body=payload.read_bytes())[0])
            finally:
                run("docker", "stop", proxy, upstream, check=False)
                run("docker", "network", "rm", network, check=False)


if __name__ == "__main__":
    unittest.main()
