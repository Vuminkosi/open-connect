"""Model-init entrypoint: records the opt-in manifest without network downloads.

Only the 1-2 configured language checkpoints are staged; the full MMS
catalogue (~1107 languages) is never pulled.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

from openconnect_mms_tts.config import MAX_OPT_IN_LANGUAGES


def main() -> None:
    if os.environ.get("OC_MODEL_PROVISIONING", "").strip().lower() == "disabled":
        print("mms-tts init skipped (OC_MODEL_PROVISIONING=disabled)")
        return
    cache_dir = os.environ.get("OC_MODEL_CACHE_DIR") or os.environ.get("MODEL_DIR", "/models")
    model_dir = Path(cache_dir)
    model_dir.mkdir(parents=True, exist_ok=True)
    raw = os.environ.get("MMS_LANGUAGES", "eng")
    languages = [lang.strip().lower() for lang in raw.split(",") if lang.strip()] or ["eng"]
    if len(languages) > MAX_OPT_IN_LANGUAGES:
        raise SystemExit(
            f"refusing init: at most {MAX_OPT_IN_LANGUAGES} opt-in languages allowed, "
            f"got {len(languages)}"
        )
    marker = model_dir / "mms-init.json"
    marker.write_text(
        json.dumps({"worker": "mms-tts", "optInLanguages": languages, "downloaded": False}) + "\n",
        encoding="utf-8",
    )
    print(f"mms-tts init complete dir={model_dir} optInLanguages={languages}")


if __name__ == "__main__":
    main()
