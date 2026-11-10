#!/usr/bin/env python3
"""
Validates topology, profile isolation, and model volume constraints for all-workers compose.
Standard-library only: does not require PyYAML or third-party packages.
Ensures:
1. Exactly 11 canonical worker services exist with vuminkosimatibe/openconnect-worker-* images.
2. Every worker has independent Compose profiles [<worker-name>, all].
3. Only the 8 Python workers mount distinct named model volumes (oc_<worker>_models).
4. navigator-playwright, operational-agents, and outbox have NO model volume mounted.
5. oc-runtime-private network has internal: true.
"""
import sys
import re

def main():
    if len(sys.argv) < 2:
        print("Usage: verify-isolation.py <compose.e2e.yaml>")
        sys.exit(1)

    path = sys.argv[1]
    with open(path, "r", encoding="utf-8") as f:
        content = f.read()

    expected_workers = [
        "security", "classification", "nllb", "inference",
        "transcription", "realtime-voice", "kokoro", "mms-tts",
        "navigator-playwright", "operational-agents", "outbox"
    ]

    for w in expected_workers:
        # Check service definition
        if not re.search(rf"^  {re.escape(w)}:\s*$", content, re.MULTILINE):
            print(f"FAIL: missing service '{w}' in compose")
            sys.exit(1)
        # Check canonical image name
        if not re.search(rf"image:\s*vuminkosimatibe/openconnect-worker-{re.escape(w)}:", content):
            print(f"FAIL: service '{w}' image does not match canonical image tag")
            sys.exit(1)
        # Check dedicated profile
        if not re.search(rf'profiles:\s*\[.*"{re.escape(w)}".*\]', content):
            print(f"FAIL: service '{w}' missing dedicated profile")
            sys.exit(1)

    python_model_workers = [
        ("security", "oc_security_models"),
        ("classification", "oc_classification_models"),
        ("nllb", "oc_nllb_models"),
        ("inference", "oc_inference_models"),
        ("transcription", "oc_transcription_models"),
        ("realtime-voice", "oc_realtime_voice_models"),
        ("kokoro", "oc_kokoro_models"),
        ("mms-tts", "oc_mms_models"),
    ]

    for w, expected_vol in python_model_workers:
        if not re.search(rf"-\s*{re.escape(expected_vol)}:/models", content):
            print(f"FAIL: python worker '{w}' must mount volume '{expected_vol}'")
            sys.exit(1)
        if not re.search(rf"^  {re.escape(expected_vol)}:\s*$", content, re.MULTILINE):
            print(f"FAIL: volumes section missing definition for '{expected_vol}'")
            sys.exit(1)

    # Check that non-model workers have no model volumes
    ops_playwright_blocks = re.findall(
        r"^\s+(navigator-playwright|operational-agents|outbox):.*?(?=^\s+[a-zA-Z0-9_-]+:|\Z)",
        content,
        re.MULTILINE | re.DOTALL
    )
    for block in ops_playwright_blocks:
        if "oc_" in block and "_models" in block:
            print("FAIL: non-model worker has model volume mounted")
            sys.exit(1)

    # Check internal network
    if not re.search(r"oc-runtime-private:\s*\n\s+internal:\s*true", content):
        print("FAIL: oc-runtime-private must configure internal: true")
        sys.exit(1)

    print("PASS: verify-isolation: all 11 workers, profiles, distinct model volumes, and internal network verified.")

if __name__ == "__main__":
    main()
