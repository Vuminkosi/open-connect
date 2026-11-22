"""Structured extraction adapter for an already provisioned local llama.cpp model.

The caller owns model provisioning, license policy, privacy authorization,
resource admission and lifecycle. This adapter never fetches model assets and
does not invoke the LiveUI action planner.
"""

from __future__ import annotations

import json
import math
import re
import threading
from typing import Any, Protocol

from openconnect_inference.extraction import MAX_OUTPUT_BYTES, ExtractionError, _compile_schema, _unique_object

_NUMERIC_SOURCE = re.compile(
    r"(-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?)(?:\s+(kg|g|mg|lb|lbs|t|ton|tons|tonne|tonnes|mm|cm|m|km|l|ml))?\Z",
    re.IGNORECASE,
)


class LocalChatModel(Protocol):
    def create_chat_completion(self, **kwargs: Any) -> dict[str, Any]: ...


class LlamaExtractionProvider:
    """One non-concurrent, bounded structured completion from a local model."""

    def __init__(self, model: LocalChatModel, *, max_tokens: int = 2048):
        if (model is None or isinstance(max_tokens, bool) or not isinstance(max_tokens, int)
                or not 1 <= max_tokens <= 4096):
            raise ValueError("invalid_model_configuration")
        self._model = model
        self._max_tokens = max_tokens
        self._slot = threading.Lock()

    def complete(self, *, system: str, document: str, schema: dict[str, Any], attempt: int) -> str:
        fields = _compile_schema(schema)
        output_schema = {"type": "object", "additionalProperties": False, "required": list(fields),
                         "properties": {name: {"type": ["string", "null"], "maxLength": 8192}
                                        for name in fields}}
        if not self._slot.acquire(blocking=False):
            raise ExtractionError("provider_unavailable")
        try:
            result = self._model.create_chat_completion(
                messages=[{"role": "system", "content": system +
                           " For each requested field copy only the exact text representing its value "
                           "from the document. If missing return null. /no_think"},
                          {"role": "user", "content": json.dumps({"fields": list(fields),
                                                                  "document": document}, ensure_ascii=True)}],
                response_format={"type": "json_object", "schema": output_schema},
                max_tokens=self._max_tokens, temperature=0, top_p=1,
            )
            content = result["choices"][0]["message"]["content"]
            if not isinstance(content, str) or len(content.encode("utf-8")) > MAX_OUTPUT_BYTES:
                raise ExtractionError("invalid_provider_output")
            try:
                raw = json.loads(content, object_pairs_hook=_unique_object,
                                 parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
                if not isinstance(raw, dict) or set(raw) != set(fields):
                    raise ValueError()
                converted = {}
                for name, source in raw.items():
                    if source is None:
                        converted[name] = {"value": None, "start": None, "end": None}
                        continue
                    if not isinstance(source, str) or not 1 <= len(source) <= 8192:
                        raise ValueError()
                    start = document.find(source)
                    if start < 0 or document.find(source, start + 1) != -1:
                        raise ValueError()
                    kind = fields[name].kind
                    if kind == "string":
                        value = source
                    elif kind == "boolean" and source.casefold() in {"true", "false"}:
                        value = source.casefold() == "true"
                    elif kind in {"number", "integer"} and (match := _NUMERIC_SOURCE.fullmatch(source)):
                        # The model may cite a value with its unit; the numeric field's
                        # source span must cover only the literal number itself.
                        source = match.group(1)
                        value = int(source) if "." not in source else float(source)
                        if (isinstance(value, float) and not math.isfinite(value)) or (kind == "integer" and "." in source):
                            raise ValueError()
                    else:
                        raise ValueError()
                    converted[name] = {"value": value, "start": start, "end": start + len(source)}
                return json.dumps({"fields": converted}, ensure_ascii=True, allow_nan=False)
            except (ValueError, TypeError, RecursionError):
                raise ExtractionError("invalid_provider_output") from None
        except ExtractionError:
            raise
        except Exception:
            raise ExtractionError("provider_unavailable") from None
        finally:
            self._slot.release()
