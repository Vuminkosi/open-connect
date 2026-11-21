"""Deployment-owned credentials for the optional Cloud worker boundary."""

import os
from functools import lru_cache
from pathlib import Path

from openconnect_nllb.cloud_consumption import CloudConsumptionClient


@lru_cache(maxsize=1)
def configured_client() -> CloudConsumptionClient | None:
    enabled = os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower()
    if enabled == "false":
        return None
    if enabled != "true":
        raise ValueError("invalid cloud consumption configuration")
    values = [os.environ.get(name) for name in (
        "OPENCONNECT_CLOUD_AUTHORITY_ORIGIN", "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE",
        "OPENCONNECT_CLOUD_WORKER_CERT_FILE", "OPENCONNECT_CLOUD_WORKER_KEY_FILE",
    )]
    if not all(values):
        raise ValueError("incomplete cloud consumption configuration")
    origin, ca, cert, key = values
    assert origin is not None and ca is not None and cert is not None and key is not None
    return CloudConsumptionClient(origin, Path(ca), Path(cert), Path(key))
