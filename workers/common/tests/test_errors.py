"""Error contract tests: stable codes and safe payloads."""

from openconnect_worker_common.contracts.errors import ErrorCode, WorkerError


def test_stable_error_codes() -> None:
    assert {c.value for c in ErrorCode} == {
        "task_not_found",
        "session_not_found",
        "unauthorized",
        "tool_not_allowed",
        "approval_required",
        "capability_unavailable",
        "privacy_blocked",
        "invalid_request",
        "conflict",
        "rate_limited",
    }


def test_safe_wire_shape_has_no_internals() -> None:
    error = WorkerError(code="privacy_blocked", message="blocked by policy", retryable=False)
    payload = error.to_dict()
    assert payload == {
        "code": "privacy_blocked",
        "message": "blocked by policy",
        "retryable": False,
    }
    assert "stack" not in payload and "trace" not in str(payload)


def test_correlation_id_included_when_present() -> None:
    error = WorkerError(code="conflict", message="stale lease", correlationId="corr-1")
    assert error.to_dict()["correlationId"] == "corr-1"
