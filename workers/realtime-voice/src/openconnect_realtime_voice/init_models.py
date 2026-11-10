"""Model-init entrypoint: prepares the model volume without network downloads.

Orchestration-only mode requires no local weights; this step only ensures the
mounted volume exists and records the worker mode marker for serve.
"""

from __future__ import annotations

import os
from pathlib import Path


def main() -> None:
    model_dir = Path(os.environ.get("MODEL_DIR", "/models"))
    model_dir.mkdir(parents=True, exist_ok=True)
    orchestration_only = os.environ.get("OC_REALTIME_ORCHESTRATION_ONLY", "1").strip().lower() in {
        "1",
        "true",
        "yes",
        "on",
    }
    marker = model_dir / "realtime-voice-init.json"
    marker.write_text(
        '{"worker": "realtime-voice", "orchestrationOnly": %s, "downloaded": false}\n'
        % ("true" if orchestration_only else "false"),
        encoding="utf-8",
    )
    print(f"realtime-voice init complete dir={model_dir} orchestrationOnly={orchestration_only}")


if __name__ == "__main__":
    main()
