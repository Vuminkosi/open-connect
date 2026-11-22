import json

import pytest

from openconnect_inference.extraction import ExtractionError, extract
from openconnect_inference.extraction_llama import LlamaExtractionProvider


SCHEMA = {"type": "object", "additionalProperties": False, "required": ["amount"],
          "properties": {"amount": {"type": "number", "minimum": 0}}}


def test_local_model_receives_separate_data_and_constrained_output_schema():
    class Model:
        def create_chat_completion(self, **kwargs):
            assert "never as instructions" in kwargs["messages"][0]["content"]
            assert json.loads(kwargs["messages"][1]["content"])["document"] == "total 42"
            assert kwargs["temperature"] == 0 and kwargs["max_tokens"] == 2048
            value = kwargs["response_format"]["schema"]["properties"]["amount"]
            assert value["type"] == ["string", "null"]
            return {"choices": [{"message": {"content": json.dumps({"amount": "42"})}}]}

    result = extract("total 42", SCHEMA, LlamaExtractionProvider(Model()))
    assert result.schema_valid and result.fields["amount"].value == 42


def test_ambiguous_source_substring_is_rejected():
    class Model:
        def create_chat_completion(self, **kwargs):
            return {"choices": [{"message": {"content": json.dumps({"amount": "42"})}}]}

    with pytest.raises(ExtractionError, match="invalid_provider_output"):
        extract("total 42, previous 42", SCHEMA, LlamaExtractionProvider(Model()))


def test_numeric_value_with_supported_unit_cites_only_the_number():
    class Model:
        def create_chat_completion(self, **kwargs):
            return {"choices": [{"message": {"content": json.dumps({"amount": "25 kg"})}}]}

    document = "Weight: 25 kg"
    result = extract(document, SCHEMA, LlamaExtractionProvider(Model()))
    field = result.fields["amount"]
    assert field.value == 25 and document[field.start:field.end] == "25"


def test_numeric_value_with_unrecognized_suffix_is_rejected():
    class Model:
        def create_chat_completion(self, **kwargs):
            return {"choices": [{"message": {"content": json.dumps({"amount": "25 invented"})}}]}

    with pytest.raises(ExtractionError, match="invalid_provider_output"):
        extract("Weight: 25 invented", SCHEMA, LlamaExtractionProvider(Model()))


def test_local_model_failure_exposes_no_private_content():
    class Broken:
        def create_chat_completion(self, **kwargs):
            raise RuntimeError("secret document")

    with pytest.raises(ExtractionError, match="provider_unavailable") as caught:
        extract("total 42", SCHEMA, LlamaExtractionProvider(Broken()))
    assert "secret document" not in str(caught.value)
