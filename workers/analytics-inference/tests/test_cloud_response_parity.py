"""Exercise the gateway response checks against actual worker calculations."""

import json
import shutil
import subprocess
from dataclasses import asdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_analytics_inference.forecast import forecast
from openconnect_analytics_inference.statistics import analyze
from openconnect_analytics_inference.trend import trend

ID = "11111111-1111-1111-1111-111111111111"
ROOT = Path(__file__).resolve().parents[3]


@pytest.mark.parametrize("name", ["cloud-worker-analytics-response-v1", "cloud-analytics-response-v1"])
def test_cloud_usage_response_contract_fixtures(name):
    root = ROOT / "contracts"
    schema = json.loads((root / "jsonschema" / f"{name}.json").read_text(encoding="utf-8"))
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    valid = json.loads((root / "fixtures" / "valid" / f"{name}.json").read_text(encoding="utf-8"))
    invalid = json.loads((root / "fixtures" / "invalid" / f"{name}.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator.validate(valid)
    assert not validator.is_valid(invalid)
    expected = json.loads(json.dumps(asdict(analyze([1, 2, 3]))))
    assert {key: valid["result"][key] for key in expected} == expected


@pytest.mark.skipif(shutil.which("node") is None, reason="Node.js is unavailable")
@pytest.mark.parametrize("mode", ["forecast", "missing", "irregular", "anomaly"])
def test_python_results_match_gateway_validation(mode):
    values = [1.0, 2.0] * 6
    if mode == "missing":
        values[4] = None
    if mode == "anomaly":
        values = [1.0, 2.0, 3.0, 4.0, 5.0, 100.0]
    start = datetime(2030, 1, 1, tzinfo=timezone.utc)
    times = [start + timedelta(seconds=i) for i in range(len(values))]
    if mode == "irregular":
        times[-1] += timedelta(seconds=3)
    settings = {"timestamps": [t.isoformat().replace("+00:00", ".000Z") for t in times],
                "intervalSeconds": 1, "seasonalPeriod": 2, "horizon": 2}
    input_value = {"feature": "weight", "unit": "kg", "lineage": "fixture", "values": values, "trend": True}
    if mode != "anomaly":
        input_value["forecast"] = settings
    request = {"requestId": ID, "input": input_value}
    result = {"feature": "weight", "unit": "kg", "lineage": "fixture", **asdict(analyze(values)),
              "trend": asdict(trend(values))}
    if mode != "anomaly":
        result["forecast"] = asdict(forecast(values, times, interval_seconds=1, seasonal_period=2, horizon=2))
    response = {"schemaVersion": "openconnect.v1", "taskId": ID, "kind": "analytics.analyze", "result": result}
    script = """
import { readFileSync } from 'node:fs';
import { validateAnalyticsResponse } from './cloud/gateway/analytics-response.mjs';
const fixture = JSON.parse(readFileSync(0, 'utf8'));
if (!validateAnalyticsResponse(Buffer.from(JSON.stringify(fixture.response)), fixture.request)) process.exit(1);
fixture.response.result.coverage = -1;
if (validateAnalyticsResponse(Buffer.from(JSON.stringify(fixture.response)), fixture.request)) process.exit(2);
"""
    subprocess.run(["node", "--input-type=module", "-e", script], cwd=ROOT, input=json.dumps({
        "request": request, "response": response,
    }), text=True, capture_output=True, check=True, timeout=15)
