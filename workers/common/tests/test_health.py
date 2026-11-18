"""Health probe tests."""

from openconnect_worker_common.health import liveness, readiness, readiness_status


def test_liveness() -> None:
    assert liveness() == {"status": "live"}


def test_readiness_lists_capabilities() -> None:
    assert readiness(["rules"]) == {"status": "ready", "capabilities": ["rules"]}
    assert readiness() == {"status": "ready", "capabilities": []}


def test_readiness_status_fails_closed() -> None:
    code, body = readiness_status({"rules": True})
    assert code == 200
    assert body["status"] == "ready"
    code, body = readiness_status({"rules": True, "qwen3guard": False})
    assert code == 503
    assert body == {"status": "not-ready", "failed": ["qwen3guard"]}
