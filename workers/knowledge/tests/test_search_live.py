"""Opt-in signed search smoke against a disposable, seeded worker deployment."""

import json
import os
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_worker_common.security.signing import sign_bytes

_NAMES = ["OC_TEST_SEARCH_ENDPOINT", "OC_TEST_SEARCH_TOKEN", "OC_TEST_SEARCH_KEY", "OC_TEST_SEARCH_TENANT"]
pytestmark = pytest.mark.skipif(any(not os.getenv(name) for name in _NAMES),
                                reason="Set disposable search deployment and fixture credentials")


def test_live_signed_search_is_tenant_and_acl_scoped():
    endpoint = os.environ["OC_TEST_SEARCH_ENDPOINT"].rstrip("/") + "/internal/v1/execute"
    key = bytes.fromhex(os.environ["OC_TEST_SEARCH_KEY"])
    now = datetime.now(timezone.utc)
    request = {"schemaVersion": "openconnect.v1", "taskId": "live-smoke", "kind": "knowledge.search",
               "tenantId": os.environ["OC_TEST_SEARCH_TENANT"], "issuedAt": (now - timedelta(seconds=1)).isoformat(),
               "expiresAt": (now + timedelta(minutes=1)).isoformat(), "payload": {
                   "applicationId": "app", "collectionId": "collection", "principalId": "alice", "query": "planet"}}

    def submit(data, *, bad_signature=False):
        body = json.dumps(data).encode()
        headers = {"Authorization": "Bearer " + os.environ["OC_TEST_SEARCH_TOKEN"],
                   "X-OpenConnect-Signature": "0" * 64 if bad_signature else sign_bytes(body, key),
                   "Content-Type": "application/json"}
        try:
            with urllib.request.urlopen(urllib.request.Request(endpoint, body, headers, method="POST"), timeout=30) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as exc:
            return exc.code, None

    status, result = submit(request)
    assert status == 200 and [item["hit"]["document_id"] for item in result["result"]["hits"]] == ["visible"]
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(request, "knowledge-search-request.json"), (result, "knowledge-search-response.json")]:
        schema = json.loads((root / name).read_text(encoding="utf-8"))
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
    request["payload"]["principalId"] = "bob"
    status, result = submit(request)
    assert status == 200 and [item["hit"]["document_id"] for item in result["result"]["hits"]] == ["hidden"]
    request["tenantId"] = "other-tenant"
    status, result = submit(request)
    assert status == 200 and not result["result"]["hits"]
    assert submit(request, bad_signature=True)[0] == 401
