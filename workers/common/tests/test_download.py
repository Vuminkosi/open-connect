import hashlib
import io
import socket
import threading
from pathlib import Path

import pytest

from openconnect_worker_common.models import download
from openconnect_worker_common.models.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)

DATA = b"verified model fixture"


def manifest() -> RuntimeModelManifest:
    return RuntimeModelManifest(
        workerKind="test", modelId="example/model", immutableRevision="abc123",
        license="mit", assets=[RuntimeModelAsset(
            relativePath="model.onnx", sha256=hashlib.sha256(DATA).hexdigest(),
            sizeBytes=len(DATA),
        )],
    )


class Response:
    status = 200

    def __init__(self, data: bytes = DATA, content_type: str = "application/octet-stream"):
        self.data = io.BytesIO(data)
        self.content_type = content_type
        self.length = len(data)

    def getheader(self, name: str, default: str | None = None) -> str | None:
        return {"Content-Type": self.content_type, "Content-Length": str(self.length)}.get(
            name, default
        )

    def read1(self, amount: int) -> bytes:
        return self.data.read(amount)


class Connection:
    response = Response()
    calls = 0
    sock = None

    def __init__(self, host: str, address: str, timeout: float):
        assert host == "models.example" and address == "93.184.216.34"

    def request(self, method: str, path: str, headers: dict) -> None:
        assert method == "GET" and path == "/revision/model.onnx"
        Connection.calls += 1

    def getresponse(self) -> Response:
        return Connection.response

    def close(self) -> None:
        pass


@pytest.fixture
def transport(monkeypatch: pytest.MonkeyPatch) -> None:
    Connection.response = Response()
    Connection.calls = 0
    monkeypatch.setattr(download, "_PinnedHTTPSConnection", Connection)
    monkeypatch.setattr(socket, "getaddrinfo", lambda *a, **k: [
        (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("93.184.216.34", 443))
    ])


def fetcher(model: RuntimeModelManifest, *, url: str = "https://models.example/revision/model.onnx"):
    return download.StartupAssetFetcher(
        model, {"model.onnx": download.AssetSource(url, frozenset({"application/octet-stream"}))},
        frozenset({"models.example"}), max_bytes=1024, timeout_seconds=10,
    )


def test_first_start_and_offline_restart(tmp_path: Path, transport: None) -> None:
    model = manifest()
    manager = RuntimeModelCacheManager(tmp_path, "test")
    result = manager.provision_or_verify(model, asset_fetcher=fetcher(model))
    assert result.ready and result.downloads_attempted == 1
    assert Connection.calls == 1
    result = manager.provision_or_verify(model, offline=True, asset_fetcher=fetcher(model))
    assert result.ready and result.cache_hit and result.downloads_attempted == 0
    assert Connection.calls == 1


@pytest.mark.parametrize("url", [
    "http://models.example/revision/model.onnx", "https://evil.example/a",
    "https://models.example:444/a", "https://user@models.example/a",
])
def test_unapproved_urls_rejected(tmp_path: Path, transport: None, url: str) -> None:
    model = manifest()
    with pytest.raises(ValueError, match="unapproved_source"):
        fetcher(model, url=url)(model.assets[0], tmp_path / "model")
    assert Connection.calls == 0


def test_private_dns_address_rejected(tmp_path: Path, transport: None, monkeypatch) -> None:
    monkeypatch.setattr(socket, "getaddrinfo", lambda *a, **k: [
        (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("127.0.0.1", 443))
    ])
    model = manifest()
    with pytest.raises(ValueError, match="unapproved_address"):
        fetcher(model)(model.assets[0], tmp_path / "model")


@pytest.mark.parametrize("failure", ["redirect", "mime", "size", "digest"])
def test_bad_response_never_activates(tmp_path: Path, transport: None, failure: str) -> None:
    if failure == "redirect":
        Connection.response.status = 302
    elif failure == "mime":
        Connection.response.content_type = "text/html"
    elif failure == "size":
        Connection.response.length = len(DATA) + 1
    else:
        Connection.response.data = io.BytesIO(b"x" * len(DATA))
    model = manifest()
    result = RuntimeModelCacheManager(tmp_path, "test").provision_or_verify(
        model, asset_fetcher=fetcher(model)
    )
    assert not result.ready and result.reason == "provision_failed"


def test_unapproved_asset_and_budget_rejected(tmp_path: Path, transport: None) -> None:
    model = manifest()
    altered = model.assets[0].model_copy(update={"sha256": "0" * 64})
    with pytest.raises(ValueError, match="unapproved_asset"):
        fetcher(model)(altered, tmp_path / "model")
    with pytest.raises(ValueError, match="download_budget_exceeded"):
        download.StartupAssetFetcher(model, {"model.onnx": download.AssetSource(
            "https://models.example/a", frozenset({"application/octet-stream"})
        )}, frozenset({"models.example"}), max_bytes=1, timeout_seconds=1)


def test_dns_timeout_does_not_start_repeated_resolvers(tmp_path: Path, monkeypatch) -> None:
    release = threading.Event()
    calls: list[int] = []

    def stalled(*args, **kwargs):
        calls.append(1)
        release.wait(2)
        return []

    monkeypatch.setattr(socket, "getaddrinfo", stalled)
    model = manifest()
    downloader = download.StartupAssetFetcher(
        model, {"model.onnx": download.AssetSource("https://models.example/a", frozenset({
            "application/octet-stream"
        }))}, frozenset({"models.example"}), max_bytes=1024, timeout_seconds=0.05,
    )
    try:
        for _ in range(2):
            with pytest.raises(TimeoutError, match="source_resolution_timeout"):
                downloader(model.assets[0], tmp_path / "model")
        assert len(calls) == 1
    finally:
        release.set()


@pytest.mark.parametrize("location,allowed,expected", [
    ("https://cdn.example/model", {"models.example", "cdn.example"}, True),
    ("https://evil.example/model", {"models.example"}, False),
    ("http://cdn.example/model", {"models.example", "cdn.example"}, False),
])
def test_redirects_revalidate_hosts_and_scheme(tmp_path: Path, monkeypatch, location, allowed, expected):
    seen: list[str] = []

    class Redirect(Response):
        status = 302

        def getheader(self, name, default=None):
            return location if name == "Location" else super().getheader(name, default)

    class RedirectConnection:
        sock = None

        def __init__(self, host, address, timeout):
            self.host = host
            seen.append(host)

        def request(self, method, path, headers):
            pass

        def getresponse(self):
            return Redirect() if self.host == "models.example" else Response()

        def close(self):
            pass

    monkeypatch.setattr(download, "_PinnedHTTPSConnection", RedirectConnection)
    monkeypatch.setattr(socket, "getaddrinfo", lambda *a, **k: [
        (socket.AF_INET, socket.SOCK_STREAM, 6, "", ("93.184.216.34", 443))
    ])
    model = manifest()
    downloader = download.StartupAssetFetcher(model, {"model.onnx": download.AssetSource(
        "https://models.example/model", frozenset({"application/octet-stream"})
    )}, frozenset(allowed), max_bytes=1024, timeout_seconds=10)
    if expected:
        downloader(model.assets[0], tmp_path / "model")
        assert (tmp_path / "model").read_bytes() == DATA
        assert seen == ["models.example", "cdn.example"]
    else:
        with pytest.raises(ValueError, match="unapproved_source"):
            downloader(model.assets[0], tmp_path / "model")
        assert seen == ["models.example"]
