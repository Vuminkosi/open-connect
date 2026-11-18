"""Shared contracts, security, transport, and model helpers for OpenConnect workers.

This package is dependency-light (pydantic v2 + stdlib only at runtime) and
performs no network, database, vault, or model-download I/O on import.
"""

from openconnect_worker_common.health import liveness, readiness

__version__ = "0.1.0"

__all__ = ["__version__", "liveness", "readiness"]
