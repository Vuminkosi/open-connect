import base64
import json
from pathlib import Path

import pytest

from openconnect_knowledge.document_parser import DocumentOcrParser

TENANT = "01ARZ3NDEKTSV4RRFFQ69G5FAV"


def test_rejects_mutable_or_subclassed_bytes_before_worker_request(monkeypatch):
    class ByteSubclass(bytes):
        pass

    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32)
    sent = []
    monkeypatch.setattr(parser, "_post", lambda body: sent.append(body))
    for content in (bytearray(b"document"), ByteSubclass(b"document")):
        with pytest.raises(ValueError, match="invalid_document"):
            parser.parse(TENANT, content)
    assert sent == []


def page():
    return {"text": "Hello world", "width": 100, "height": 50, "model_id": "ocr",
            "model_fingerprint": "a" * 64, "language": "eng", "spans": [
                {"text": "Hello", "start": 0, "end": 5, "box": [0.0, 0.0, 0.4, 1.0], "confidence": 0.9},
                {"text": "world", "start": 6, "end": 11, "box": [0.5, 0.0, 1.0, 1.0], "confidence": 0.8}]}


def test_builds_tenant_bound_request_and_preserves_page_text(monkeypatch):
    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32)

    def response(body):
        request = json.loads(body)
        assert request["tenantId"] == TENANT and request["kind"] == "documents.ocr"
        assert base64.b64decode(request["payload"]["contentBase64"]) == b"document"
        return json.dumps({"schemaVersion": "openconnect.v1", "taskId": request["taskId"],
                           "kind": "documents.ocr", "result": {"pages": [page()]}}).encode()

    monkeypatch.setattr(parser, "_post", response)
    pages = parser.parse(TENANT, b"document")
    assert len(pages) == 1 and pages[0].page_id == "page-1" and pages[0].text == "Hello world"


@pytest.mark.parametrize("mutation", ["task", "extra", "span", "geometry", "language", "missing", "duplicate", "bool"])
def test_worker_response_cannot_inject_unverified_text(monkeypatch, mutation):
    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32)

    def response(body):
        request = json.loads(body)
        item = page()
        value = {"schemaVersion": "openconnect.v1", "taskId": request["taskId"], "kind": "documents.ocr",
                 "result": {"pages": [item]}}
        if mutation == "task":
            value["taskId"] = "different"
        elif mutation == "extra":
            item["unexpected"] = "private-sentinel"
        elif mutation == "span":
            item["spans"][1]["text"] = "wrong"
        elif mutation == "geometry":
            item["spans"][0]["box"] = [0.8, 0.0, 0.4, 1.0]
        elif mutation == "language":
            item["language"] = "zzz"
        elif mutation == "missing":
            item["spans"].pop()
        elif mutation == "bool":
            item["spans"][0]["confidence"] = True
        result = json.dumps(value)
        if mutation == "duplicate":
            result = result.replace('"kind": "documents.ocr"', '"kind": "documents.ocr", "kind": "documents.ocr"')
        return result.encode()

    monkeypatch.setattr(parser, "_post", response)
    with pytest.raises(RuntimeError, match="invalid_worker_response") as caught:
        parser.parse(TENANT, b"document")
    assert "private-sentinel" not in str(caught.value)


def test_rejects_invalid_tenant_before_transport(monkeypatch):
    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32)
    monkeypatch.setattr(parser, "_post", lambda _: pytest.fail("transport invoked"))
    with pytest.raises(ValueError, match="invalid_document"):
        parser.parse("../tenant", b"document")


def test_response_headers_use_only_remaining_deadline(monkeypatch):
    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32, timeout_seconds=10)
    times = iter([100.0, 106.0, 107.0, 108.0])
    monkeypatch.setattr("openconnect_knowledge.document_parser.time.monotonic", lambda: next(times))
    timeouts = []

    class Socket:
        def settimeout(self, value):
            timeouts.append(value)

    class Response:
        status = 200

        def read1(self, size):
            return b""

    class Connection:
        sock = Socket()

        def __init__(self, path, timeout):
            assert timeout == 10

        def request(self, *args, **kwargs):
            pass

        def getresponse(self):
            assert timeouts == [4.0]
            return Response()

        def close(self):
            pass

    monkeypatch.setattr("openconnect_knowledge.document_parser._UnixConnection", Connection)
    assert parser._post(b"request") == b""
    assert timeouts == [4.0, 3.0]


def test_expired_deadline_does_not_wait_for_response_headers(monkeypatch):
    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32, timeout_seconds=10)
    times = iter([100.0, 110.0])
    monkeypatch.setattr("openconnect_knowledge.document_parser.time.monotonic", lambda: next(times))

    class Connection:
        sock = object()

        def __init__(self, path, timeout):
            pass

        def request(self, *args, **kwargs):
            pass

        def getresponse(self):
            pytest.fail("response was read after deadline")

        def close(self):
            pass

    monkeypatch.setattr("openconnect_knowledge.document_parser._UnixConnection", Connection)
    with pytest.raises(RuntimeError, match="document_worker_unavailable"):
        parser._post(b"request")


def test_deadline_aborts_in_progress_header_read(monkeypatch):
    parser = DocumentOcrParser(Path.cwd() / "worker.sock", "token", b"x" * 32, timeout_seconds=10)
    timers = []
    events = []

    class Timer:
        daemon = False

        def __init__(self, timeout, callback):
            self.callback = callback
            timers.append(self)

        def start(self):
            pass

        def cancel(self):
            events.append("cancel")

    class Socket:
        def settimeout(self, value):
            pass

        def shutdown(self, how):
            events.append("shutdown")

        def close(self):
            events.append("socket-close")

    class Connection:
        sock = Socket()

        def __init__(self, path, timeout):
            pass

        def request(self, *args, **kwargs):
            pass

        def getresponse(self):
            timers[0].callback()
            raise OSError("private transport error")

        def close(self):
            events.append("connection-close")

    monkeypatch.setattr("openconnect_knowledge.document_parser.threading.Timer", Timer)
    monkeypatch.setattr("openconnect_knowledge.document_parser._UnixConnection", Connection)
    with pytest.raises(RuntimeError, match="document_worker_unavailable") as caught:
        parser._post(b"request")
    assert "private transport error" not in str(caught.value)
    assert events == ["shutdown", "socket-close", "cancel", "connection-close"]
