"""Internal TIFF renderer; the parent must provide an OS sandbox."""

import base64
import io
import json
import sys
import warnings

from PIL import Image, ImageOps, UnidentifiedImageError

MAX_BYTES = 10 * 1024 * 1024
MAX_PIXELS = 16_000_000
MAX_PAGES = 30


def main() -> None:
    try:
        index = int(sys.argv[1])
        content = sys.stdin.buffer.read(MAX_BYTES + 1)
        if len(content) > MAX_BYTES or not content.startswith((b"II*\x00", b"MM\x00*")):
            raise ValueError("invalid_document")
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(content)) as image:
                if image.format != "TIFF":
                    raise ValueError("invalid_document")
                count = image.n_frames
                if not 1 <= count <= MAX_PAGES:
                    raise ValueError("page_limit_exceeded")
                if not 0 <= index < count:
                    raise ValueError("invalid_page")
                image.seek(index)
                if image.width <= 0 or image.height <= 0 or image.width * image.height > MAX_PIXELS:
                    raise ValueError("page_limit_exceeded")
                image.load()
                normalized = ImageOps.exif_transpose(image).convert("RGB")
                output = io.BytesIO()
                normalized.save(output, format="PNG")
                result = {"pageCount": count, "pageIndex": index,
                          "png": base64.b64encode(output.getvalue()).decode("ascii")}
    except (ValueError, IndexError, OSError, EOFError, SyntaxError, UnidentifiedImageError,
            Image.DecompressionBombError, Image.DecompressionBombWarning) as exc:
        reason = str(exc)
        if reason not in {"invalid_document", "page_limit_exceeded", "invalid_page"}:
            reason = "invalid_document"
        result = {"error": reason}
    sys.stdout.write(json.dumps(result, separators=(",", ":")))


if __name__ == "__main__":
    main()
