import socket
import struct
from types import SimpleNamespace

import pytest

from openconnect_worker_common.security import sandbox
from openconnect_worker_common.security.clamav import (
    ClamAvScanner,
    MalwareDetectedError,
    MalwareScanUnavailableError,
)


class Connection:
    def __init__(self, reply):
        self.reply = reply
        self.sent = bytearray()

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        pass

    def settimeout(self, value):
        assert value > 0

    def connect(self, _path):
        pass

    def sendall(self, value):
        self.sent.extend(value)

    def recv(self, count):
        value, self.reply = self.reply[:count], self.reply[count:]
        return value


def test_scanner_frames_chunks_and_accepts_only_clean_response(monkeypatch, tmp_path):
    connection = Connection(b"stream: OK\0")
    monkeypatch.setattr(socket, "AF_UNIX", getattr(socket, "AF_UNIX", 1), raising=False)
    monkeypatch.setattr(socket, "socket", lambda *_args: connection)
    ClamAvScanner(tmp_path / "clamd.sock").scan(b"x" * 70000)
    assert connection.sent.startswith(b"zINSTREAM\0" + struct.pack("!I", 65536))
    assert connection.sent.endswith(struct.pack("!I", 0))


@pytest.mark.parametrize("reply,error", [
    (b"stream: private-signature FOUND\0", MalwareDetectedError),
    (b"stream: error ERROR\0", MalwareScanUnavailableError),
    (b"stream: OK\n", MalwareScanUnavailableError),
    (b"x" * 4097, MalwareScanUnavailableError),
])
def test_scanner_fails_closed_without_exposing_content(monkeypatch, tmp_path, reply, error):
    monkeypatch.setattr(socket, "AF_UNIX", getattr(socket, "AF_UNIX", 1), raising=False)
    monkeypatch.setattr(socket, "socket", lambda *_args: Connection(reply))
    with pytest.raises(error) as caught:
        ClamAvScanner(tmp_path / "clamd.sock").scan(b"private document")
    assert "private" not in str(caught.value)


def test_sandbox_rejects_root_and_missing_runtime_evidence(monkeypatch):
    monkeypatch.setattr(sandbox.os, "name", "posix")
    monkeypatch.setattr(sandbox.os, "geteuid", lambda: 0, raising=False)
    assert not sandbox.restricted_media_sandbox_ready()
    monkeypatch.setattr(sandbox.os, "geteuid", lambda: 1000, raising=False)
    monkeypatch.setattr(sandbox.Path, "read_text", lambda *_args, **_kwargs: "")
    assert not sandbox.restricted_media_sandbox_ready()


def test_sandbox_requires_all_runtime_restrictions(monkeypatch):
    monkeypatch.setattr(sandbox.os, "name", "posix")
    monkeypatch.setattr(sandbox.os, "geteuid", lambda: 1000, raising=False)
    monkeypatch.setattr(sandbox.os, "ST_RDONLY", 1, raising=False)
    monkeypatch.setattr(sandbox.os, "statvfs", lambda _path: SimpleNamespace(f_flag=1), raising=False)
    monkeypatch.setattr(sandbox.os, "listdir", lambda _path: ["lo"])
    values = {"/proc/self/status": "NoNewPrivs:\t1\nSeccomp:\t2\n",
              "/sys/fs/cgroup/memory.max": "2147483648", "/sys/fs/cgroup/pids.max": "128"}
    monkeypatch.setattr(sandbox.Path, "read_text", lambda path, **_kwargs: values[str(path)])
    assert sandbox.restricted_media_sandbox_ready()
    monkeypatch.setattr(sandbox.os, "listdir", lambda _path: ["lo", "eth0"])
    assert not sandbox.restricted_media_sandbox_ready()
