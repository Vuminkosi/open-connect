"""Model-init entrypoint: prepares the model volume without network downloads.

Orchestration-only mode requires no local weights; this step only ensures the
mounted volume exists and records the worker mode marker for serve.
"""

from __future__ import annotations

import os
from pathlib import Path

from openconnect_realtime_voice.engine_mode import parse_provisioning, resolve_cache_dir


def main() -> None:
    model_dir = Path(
        resolve_cache_dir(os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR"))
    )
    provisioning = parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING"))
    orchestration_only = os.environ.get("OC_REALTIME_ORCHESTRATION_ONLY", "1").strip().lower() in {
        "1",
        "true",
        "yes",
        "on",
    }
    if provisioning == "disabled":
        print(
            f"realtime-voice init skipped (provisioning=disabled) dir={model_dir} "
            f"orchestrationOnly={orchestration_only}"
        )
        return
    model_dir.mkdir(parents=True, exist_ok=True)
    marker = model_dir / "realtime-voice-init.json"
    marker.write_text(
        '{"worker": "realtime-voice", "orchestrationOnly": %s, "provisioning": "%s", "downloaded": false}\n'
        % ("true" if orchestration_only else "false", provisioning),
        encoding="utf-8",
    )
    print(f"realtime-voice init complete dir={model_dir} orchestrationOnly={orchestration_only}")


if __name__ == "__main__":
    main()
