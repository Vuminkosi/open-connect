"""Combine source-cited document analysis components with explicit partial results."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any

from openconnect_document_intelligence.fields import extract_field_candidates
from openconnect_document_intelligence.layout import extract_line_regions
from openconnect_document_intelligence.pipeline import DocumentPageResult, DocumentResult, PartialDocumentResult
from openconnect_document_intelligence.tables import extract_table_candidates


@dataclass(frozen=True, repr=False)
class AnalysisComponent:
    status: str
    items: tuple[Any, ...]
    warnings: tuple[str, ...]


@dataclass(frozen=True, repr=False)
class DocumentAnalysis:
    pages: tuple[Any, ...]
    layout: AnalysisComponent
    tables: AnalysisComponent
    fields: AnalysisComponent
    status: str
    warnings: tuple[str, ...]


@dataclass(frozen=True, repr=False)
class PartialDocumentAnalysis:
    pages: tuple[DocumentPageResult, ...]
    layout: AnalysisComponent
    tables: AnalysisComponent
    fields: AnalysisComponent
    status: str
    warnings: tuple[str, ...]


def assemble_analysis(document: DocumentResult) -> DocumentAnalysis:
    """Keep OCR pages if a candidate extractor fails, without exposing diagnostics.

    Layout, table and field results are heuristic candidates with source-page
    references; callers must not treat them as verified semantic extraction.
    """
    if not isinstance(document, DocumentResult) or not 1 <= len(document.pages) <= 30:
        raise ValueError("invalid_document_result")
    first = document.pages[0]
    if any(page.model_id != first.model_id or page.model_fingerprint != first.model_fingerprint or page.language != first.language
           for page in document.pages):
        raise ValueError("inconsistent_ocr_provenance")

    def component(extract, item_name: str) -> AnalysisComponent:
        try:
            value = extract(document.pages)
            warnings = tuple(value.warnings)
            status = "partial" if any(code != "heuristic_tables_require_review" for code in warnings) else "completed"
            return AnalysisComponent(status, tuple(asdict(item) for item in getattr(value, item_name)), warnings)
        except Exception:
            return AnalysisComponent("unavailable", (), ("component_unavailable",))

    layout = component(extract_line_regions, "lines")
    tables = component(extract_table_candidates, "tables")
    fields = component(extract_field_candidates, "fields")
    complete = all(item.status == "completed" for item in (layout, tables, fields))
    warnings = ("heuristic_candidates_require_review",) if complete else (
        "heuristic_candidates_require_review", "component_results_incomplete")
    return DocumentAnalysis(document.pages, layout, tables, fields, "completed" if complete else "partial", warnings)


def assemble_partial_analysis(document: PartialDocumentResult) -> PartialDocumentAnalysis:
    """Preserve original page indexes when candidate extraction skips failed pages."""
    if not isinstance(document, PartialDocumentResult) or not 1 <= len(document.pages) <= 30:
        raise ValueError("invalid_document_result")
    for expected, item in enumerate(document.pages):
        if (item.page_index != expected or item.status not in {"completed", "failed"}
                or (item.status == "completed") != (item.page is not None)
                or (item.status == "completed" and item.reason is not None)
                or (item.status == "failed" and item.reason not in {"page_unavailable", "ocr_unavailable", "deadline_exceeded"})):
            raise ValueError("invalid_document_result")
    if document.status != ("completed" if all(item.status == "completed" for item in document.pages) else "partial"):
        raise ValueError("invalid_document_result")
    completed = [item for item in document.pages if item.page is not None]
    if not completed:
        unavailable = AnalysisComponent("unavailable", (), ("no_completed_pages",))
        return PartialDocumentAnalysis(document.pages, unavailable, unavailable, unavailable, "partial",
                                       ("heuristic_candidates_require_review", "page_results_incomplete"))
    combined = assemble_analysis(DocumentResult(tuple(item.page for item in completed)))

    def remap(component: AnalysisComponent) -> AnalysisComponent:
        return AnalysisComponent(component.status, tuple({**item, "page_index": completed[item["page_index"]].page_index}
                                 for item in component.items), component.warnings)

    warnings = list(combined.warnings)
    if document.status == "partial":
        warnings.append("page_results_incomplete")
    return PartialDocumentAnalysis(document.pages, remap(combined.layout), remap(combined.tables), remap(combined.fields),
                                   "partial" if document.status == "partial" or combined.status == "partial" else "completed",
                                   tuple(warnings))
