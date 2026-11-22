"""Disposable loopback-only synthetic provider for cross-language wire tests."""

import json
import os

import uvicorn

from openconnect_inference.app import app
from openconnect_inference.extraction_api import ExtractionRuntime


class FixtureProvider:
    def complete(self, *, system, document, schema, attempt):
        source = "42"
        start = document.index(source)
        return json.dumps({"fields": {"total": {"value": 42, "start": start, "end": start + 2}}})


if os.environ.get("OC_EXTRACTION_FIXTURE") != "1":
    raise RuntimeError("fixture_disabled")
app.state.extraction_runtime = ExtractionRuntime(FixtureProvider(), lambda: True)
uvicorn.run(app, host="127.0.0.1", port=int(os.environ["OC_EXTRACTION_FIXTURE_PORT"]), log_level="error")
