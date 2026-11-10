"""Model-init entrypoint: stages the voice inventory without network downloads.

Only voices listed in KOKORO_VOICES are staged; nothing is fetched from the
network here or on the synthesize path.
"""

from __future__ import annotations

import json
import os
from pathlib import Path


def main() -> None:
    model_dir = Path(os.environ.get("MODEL_DIR", "/models"))
    model_dir.mkdir(parents=True, exist_ok=True)
    raw = os.environ.get("KOKORO_VOICES", "af_heart")
    voices = [voice.strip() for voice in raw.split(",") if voice.strip()] or ["af_heart"]
    marker = model_dir / "kokoro-init.json"
    marker.write_text(
        json.dumps({"worker": "kokoro", "voices": voices, "downloaded": False}) + "\n",
        encoding="utf-8",
    )
    print(f"kokoro init complete dir={model_dir} voices={voices}")


if __name__ == "__main__":
    main()
