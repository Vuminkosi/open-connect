"""Exercise the downloader's real HTTPS socket and certificate checks locally."""

import hashlib
import http.server
import shutil
import socket
import ssl
import subprocess
import threading
from pathlib import Path

import pytest

from openconnect_worker_common.models import download
from openconnect_worker_common.models.initialize import InitializerConfig, initialize
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest

DATA = b"local tls fixture"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(DATA)))
        self.end_headers()
        self.wfile.write(DATA)

    def log_message(self, format: str, *args: object) -> None:
        pass


@pytest.fixture
def tls_server(tmp_path: Path):
    openssl = shutil.which("openssl")
    if openssl is None:
        pytest.skip("openssl is needed to create the disposable TLS certificate")
    key = tmp_path / "key.pem"
    cert = tmp_path / "cert.pem"
    subprocess.run([
        openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
        "-keyout", str(key), "-out", str(cert), "-subj", "/CN=models.example",
        "-addext", "subjectAltName=DNS:models.example",
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(cert, key)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server.server_port, cert
    finally:
        server.shutdown()
        thread.join()
        server.server_close()


def test_real_tls_download_and_hostname_verification(tmp_path: Path, tls_server, monkeypatch) -> None:
    port, cert = tls_server
    original_connect = socket.create_connection
    original_context = ssl.create_default_context
    monkeypatch.setattr(socket, "getaddrinfo", lambda *a, **k: [
        (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("93.184.216.34", 443))
    ])

    def local_connection(address, timeout):
        # Exercise the actual TLS socket on loopback after address validation.
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(timeout)
        sock.connect(("127.0.0.1", port))
        return sock

    monkeypatch.setattr(socket, "create_connection", local_connection)
    monkeypatch.setattr(ssl, "create_default_context", lambda: original_context(cafile=str(cert)))
    asset = RuntimeModelAsset(relativePath="model", sha256=hashlib.sha256(DATA).hexdigest(),
                              sizeBytes=len(DATA))
    model = RuntimeModelManifest(workerKind="test", modelId="model", immutableRevision="v1",
                                 license="mit", assets=[asset])
    source = download.AssetSource("https://models.example/model", frozenset({
        "application/octet-stream"
    }))
    fetch = download.StartupAssetFetcher(model, {"model": source}, frozenset({"models.example"}),
                                        max_bytes=1024, timeout_seconds=10)
    fetch(asset, tmp_path / "result")
    assert (tmp_path / "result").read_bytes() == DATA

    config = InitializerConfig.model_validate({
        "manifest": {"workerKind": "test", "modelId": "model", "immutableRevision": "a" * 40,
                     "license": "mit", "assets": [asset.model_dump()]},
        "sources": {"model": {"url": source.url, "mediaTypes": list(source.media_types)}},
        "allowedHosts": ["models.example"], "resourceRequest": {"memoryMb": 1},
        "resourceBudget": {"memoryMbAvailable": 2, "vramMbAvailable": 0,
                           "cpuMillicoresAvailable": 0},
        "maxDownloadBytes": 1024, "downloadTimeoutSeconds": 10,
    })
    first = initialize(config, tmp_path / "cache", provisioning="first-start-if-missing",
                       offline=False)
    assert first.ready and first.downloads_attempted == 1
    cached = initialize(config, tmp_path / "cache", provisioning="verify-existing-only",
                        offline=True)
    assert cached.ready and cached.cache_hit and cached.downloads_attempted == 0

    wrong = download.AssetSource("https://wrong.example/model", source.media_types)
    fetch = download.StartupAssetFetcher(model, {"model": wrong}, frozenset({"wrong.example"}),
                                        max_bytes=1024, timeout_seconds=10)
    with pytest.raises(ssl.SSLCertVerificationError):
        fetch(asset, tmp_path / "wrong")
    assert not (tmp_path / "wrong").exists()
    monkeypatch.setattr(socket, "create_connection", original_connect)
