"""Model-init entrypoint: stages the voice inventory without network downloads.

Only voices listed in KOKORO_VOICES / OC_KOKORO_VOICE are staged; nothing is
fetched from the network here or on the synthesize path.
"""

from __future__ import annotations

import json
import os
from pathlib import Path


def main() -> None:
    if os.environ.get("OC_MODEL_PROVISIONING", "").strip().lower() == "disabled":
        print("kokoro init skipped (OC_MODEL_PROVISIONING=disabled)")
        return
    cache_dir = os.environ.get("OC_MODEL_CACHE_DIR") or os.environ.get("MODEL_DIR", "/models")
    model_dir = Path(cache_dir)
    model_dir.mkdir(parents=True, exist_ok=True)
    raw = (
        os.environ.get("KOKORO_VOICES")
        or os.environ.get("OC_KOKORO_VOICE")
        or "af_heart"
    )
    model_id = os.environ.get("OC_KOKORO_MODEL", "hexgrad/Kokoro-82M")
    voices = [voice.strip() for voice in raw.split(",") if voice.strip()] or ["af_heart"]
    marker = model_dir / "kokoro-init.json"
    marker.write_text(
        json.dumps(
            {
                "worker": "kokoro",
                "model": model_id,
                "voices": voices,
                "downloaded": False,
            }
        )
        + "\n",
        encoding="utf-8",
    )
    print(f"kokoro init complete dir={model_dir} model={model_id} voices={voices}")


if __name__ == "__main__":
    main()
