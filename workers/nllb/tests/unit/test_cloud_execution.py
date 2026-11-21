import base64
import json
from uuid import uuid4

from openconnect_nllb.cloud_execution import execute_claimed_translation
from openconnect_nllb.schemas import ExecuteResponse, TranslateResult

REVISION = "model-revision"
TOKENIZER_HASH = "a" * 64


def fixture():
    task = str(uuid4())
    payload = json.dumps({
        "schemaVersion": "openconnect.v1", "taskId": task,
        "input": {"text": "hello", "sourceLang": "eng_Latn", "targetLang": "fra_Latn"},
    }, separators=(",", ":")).encode()
    envelope = {
        "schemaVersion": "openconnect.cloud.worker.translation/v1",
        "reservationId": str(uuid4()), "claimId": str(uuid4()),
        "payload": base64.b64encode(payload).decode(), "key": base64.b64encode(b"k" * 32).decode(),
    }
    return task, payload, envelope


class Client:
    def __init__(self, events, consumption="consumed", completion="completed"):
        self.events = events
        self.consumption = consumption
        self.completion = completion

    def consume(self, reservation_id, claim_id, payload, key):
        self.events.append(("consume", reservation_id, claim_id, payload, key))
        return self.consumption

    def complete(self, reservation_id, claim_id):
        self.events.append(("complete", reservation_id, claim_id))
        return self.completion


def response(request, **changes):
    result = dict(translatedText="bonjour", sourceLang=request.input.sourceLang,
                  targetLang=request.input.targetLang, modelRevision=REVISION,
                  tokenizerHash=TOKENIZER_HASH, engine="real", engineWarning=None)
    result.update(changes)
    return ExecuteResponse(taskId=request.taskId, status="completed", result=TranslateResult(**result))


def call(envelope, client, execute):
    return execute_claimed_translation(json.dumps(envelope).encode(), client, execute,
                                       model_revision=REVISION, tokenizer_hash=TOKENIZER_HASH)


def test_exact_payload_is_consumed_before_execution_and_settled_before_release():
    task, payload, envelope = fixture()
    events = []

    def execute(request):
        events.append(("execute", request.taskId))
        return response(request)

    result = call(envelope, Client(events), execute)
    assert result is not None and result.taskId == task and result.result.translatedText == "bonjour"
    assert [item[0] for item in events] == ["consume", "execute", "complete"]
    assert events[0][3:] == (payload, b"k" * 32)


def test_ambiguous_or_duplicate_consumption_never_runs_engine():
    for status in ("already_consumed", "unavailable", "denied"):
        _, _, envelope = fixture()
        events = []
        assert call(envelope, Client(events, consumption=status), lambda _: events.append(("execute",))) is None
        assert [item[0] for item in events] == ["consume"]


def test_invalid_or_fake_worker_output_never_settles():
    for change in ({"engine": "fake"}, {"tokenizerHash": "b" * 64}, {"sourceLang": "fra_Latn"},
                   {"translatedText": " "}, {"translatedText": "\ud800"}):
        _, _, envelope = fixture()
        events = []
        assert call(envelope, Client(events), lambda request: response(request, **change)) is None
        assert [item[0] for item in events] == ["consume"]


def test_settlement_failure_never_releases_result():
    _, _, envelope = fixture()
    events = []
    assert call(envelope, Client(events, completion="unavailable"), response) is None
    assert [item[0] for item in events] == ["consume", "complete"]


def test_invalid_envelope_and_payload_never_consume():
    _, payload, envelope = fixture()
    invalid = [
        {**envelope, "key": "invalid"},
        {**envelope, "schemaVersion": "other"},
        {**envelope, "extra": True},
        {**envelope, "payload": base64.b64encode(payload.replace(b'"hello"', b'" "')).decode()},
        {**envelope, "payload": base64.b64encode(payload.replace(b'"eng_Latn"', b'"xx"')).decode()},
        {**envelope, "payload": base64.b64encode(payload.replace(b'"hello"', b'"hello","text":"other"')).decode()},
    ]
    for value in invalid:
        events = []
        assert call(value, Client(events), response) is None
        assert not events


def test_configured_worker_text_limit_is_checked_before_consumption():
    _, _, envelope = fixture()
    events = []
    assert execute_claimed_translation(json.dumps(envelope).encode(), Client(events), response,
        model_revision=REVISION, tokenizer_hash=TOKENIZER_HASH, max_text_chars=4) is None
    assert not events
