"""Cross-runtime contract check for Cloud-to-worker analytics envelopes."""

import json
import shutil
import subprocess
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_analytics_inference.app import ExecuteRequest


@pytest.mark.skipif(shutil.which("node") is None, reason="Node.js is unavailable")
def test_cloud_gateway_payload_matches_python_and_shared_worker_contract():
    root = Path(__file__).resolve().parents[3]
    script = """
import { readFileSync } from 'node:fs';
import { prepareAnalyticsWorkerPayload } from './cloud/gateway/analytics-payload.mjs';
const body = readFileSync('./contracts/fixtures/valid/cloud-analytics-request-v1.json');
const identity = { tenantId: '00000000-0000-0000-0000-000000000001', applicationId: '22222222-2222-2222-2222-222222222222' };
const result = prepareAnalyticsWorkerPayload(body, identity, Date.parse('2030-01-01T00:00:00.000Z'));
if (!result) process.exit(1);
process.stdout.write(result.payload);
result.payload.fill(0);
"""
    result = subprocess.run(["node", "--input-type=module", "-e", script], cwd=root,
                            capture_output=True, check=True, timeout=15)
    payload = json.loads(result.stdout)
    ExecuteRequest.model_validate(payload)
    schema = json.loads((root / "contracts/jsonschema/analytics-request.json").read_text(encoding="utf-8"))
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(payload)
    assert payload["tenantId"] == "00000000000000000000000001"
    assert payload["payload"]["values"] == [1, 2, None, 4]
