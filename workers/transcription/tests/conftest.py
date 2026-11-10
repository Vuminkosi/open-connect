"""Test-only env shims so app import (Settings.from_environment at module scope)
succeeds under the fail-closed OC_* defaults.

Defaults now configure the cloud leg (OC_TRANSCRIPTION_FILE_URL), which
requires a secret-mounted key to start. Tests use a dummy value here;
individual tests override via monkeypatch for hermetic coverage. The
transcription model default stays empty: the cloud path is unready until an
exact provider model ID is set. No real credentials.
"""

import os

os.environ.setdefault("TRANSCRIPTION_CLOUD_API_KEY", "test-only-dummy-key")
