"""Internal PDF renderer. The parent must run this inside an OS sandbox."""

import base64
import io
import json
import math
import sys

import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_raw

MAX_BYTES = 10 * 1024 * 1024
MAX_PIXELS = 16_000_000
MAX_PAGES = 30
SCALE = 2


def main() -> None:
    try:
        index = int(sys.argv[1])
        content = sys.stdin.buffer.read(MAX_BYTES + 1)
        if len(content) > MAX_BYTES or not content.startswith(b"%PDF-"):
            raise ValueError("invalid_document")
        try:
            document = pdfium.PdfDocument(content)
        except pdfium.PdfiumError as exc:
            reason = "encrypted_document" if pdfium_raw.FPDF_GetLastError() == pdfium_raw.FPDF_ERR_PASSWORD else "invalid_document"
            raise ValueError(reason) from exc
        try:
            count = len(document)
            if count < 1 or count > MAX_PAGES:
                raise ValueError("page_limit_exceeded")
            if index < 0 or index >= count:
                raise ValueError("invalid_page")
            width, height = document.get_page_size(index)
            if (not math.isfinite(width) or not math.isfinite(height) or width <= 0 or height <= 0
                    or math.ceil(width * SCALE) * math.ceil(height * SCALE) > MAX_PIXELS):
                raise ValueError("page_limit_exceeded")
            page = document[index]
            try:
                bitmap = page.render(scale=SCALE, may_draw_forms=False, draw_annots=False,
                                     limit_image_cache=True)
                try:
                    image = bitmap.to_pil()
                    out = io.BytesIO()
                    image.save(out, format="PNG")
                    encoded = base64.b64encode(out.getvalue()).decode("ascii")
                finally:
                    bitmap.close()
            finally:
                page.close()
        finally:
            document.close()
        result = {"pageCount": count, "pageIndex": index, "png": encoded}
    except (ValueError, IndexError, pdfium.PdfiumError) as exc:
        reason = str(exc)
        if reason not in {"invalid_document", "encrypted_document", "page_limit_exceeded", "invalid_page"}:
            reason = "invalid_document"
        result = {"error": reason}
    sys.stdout.write(json.dumps(result, separators=(",", ":")))


if __name__ == "__main__":
    main()
