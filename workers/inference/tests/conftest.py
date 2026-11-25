"""Test-only env shims so app import (Settings.from_environment at module scope)
succeeds under the fail-closed OC_* defaults.

Defaults now configure the cloud leg (OC_INFERENCE_API_BASE_URL) with the
standard model tier. Tests use dummy values here; individual tests
override via monkeypatch for hermetic coverage. No real credentials.
"""

import os

os.environ.setdefault("INFERENCE_CLOUD_API_KEY", "test-only-dummy-key")
