"""Health probes shared by all workers: framework-agnostic liveness/readiness.

Workers expose these through their HTTP layer (FastAPI, etc.). This module
stays framework-free so importing it never pulls a web framework; use
:func:`register_fastapi_routes` for FastAPI wiring (imports FastAPI lazily).
"""

from collections.abc import Mapping


def liveness() -> dict[str, str]:
    """Liveness probe: the process is running."""
    return {"status": "live"}


def readiness(capabilities: list[str] | None = None) -> dict[str, object]:
    """Readiness probe: the worker can serve the listed capabilities."""
    return {"status": "ready", "capabilities": capabilities or []}


def readiness_status(checks: Mapping[str, bool]) -> tuple[int, dict[str, object]]:
    """Map dependency checks to an HTTP status code and body (fail closed)."""
    failed = sorted(name for name, ok in checks.items() if not ok)
    if failed:
        return 503, {"status": "not-ready", "failed": failed}
    return 200, {"status": "ready", "capabilities": sorted(checks)}


def register_fastapi_routes(app: object, capabilities: list[str] | None = None):  # type: ignore[no-untyped-def]
    """Register /health/live and /health/ready on a FastAPI app. Lazy import."""
    from fastapi import FastAPI

    if not isinstance(app, FastAPI):
        raise TypeError("app must be a FastAPI instance")
    advertised = capabilities or []

    @app.get("/health/live")
    def _live() -> dict[str, str]:
        return liveness()

    @app.get("/health/ready")
    def _ready() -> dict[str, object]:
        return readiness(list(advertised))

    return app
