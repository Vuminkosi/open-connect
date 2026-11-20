import io

import pytest
from PIL import Image
from pypdf import PdfWriter

from openconnect_document_intelligence.pdf import render_pdf_page


def make_pdf(pages: int = 1) -> bytes:
    images = [Image.new("RGB", (120, 80), "white") for _ in range(pages)]
    output = io.BytesIO()
    images[0].save(output, format="PDF", save_all=True, append_images=images[1:])
    return output.getvalue()


def test_render_actual_pdf_in_child_process() -> None:
    document = make_pdf(2)
    page = render_pdf_page(document, 1)
    assert page.page_count == 2 and page.page_index == 1
    assert page.png.startswith(b"\x89PNG\r\n\x1a\n")
    assert page.width > 0 and page.height > 0


def test_rejects_more_than_thirty_pages() -> None:
    with pytest.raises(ValueError, match="page_limit_exceeded"):
        render_pdf_page(make_pdf(31), 0)


def test_allows_thirty_pages_and_rejects_oversized_page() -> None:
    assert render_pdf_page(make_pdf(30), 29).page_count == 30
    writer = PdfWriter()
    writer.add_blank_page(width=5000, height=5000)
    output = io.BytesIO()
    writer.write(output)
    with pytest.raises(ValueError, match="page_limit_exceeded"):
        render_pdf_page(output.getvalue(), 0)


def test_rejects_missing_page_and_corrupt_pdf() -> None:
    with pytest.raises(ValueError, match="invalid_page"):
        render_pdf_page(make_pdf(), 1)
    with pytest.raises(ValueError, match="invalid_document"):
        render_pdf_page(b"%PDF-1.7\ncorrupt", 0)


def test_rejects_encrypted_pdf() -> None:
    writer = PdfWriter()
    writer.add_blank_page(width=100, height=100)
    writer.encrypt("secret")
    output = io.BytesIO()
    writer.write(output)
    with pytest.raises(ValueError, match="encrypted_document"):
        render_pdf_page(output.getvalue(), 0)


@pytest.mark.parametrize("page", [-1, 30, True])
def test_rejects_invalid_page_without_child(page) -> None:
    with pytest.raises(ValueError, match="invalid_page"):
        render_pdf_page(make_pdf(), page)
