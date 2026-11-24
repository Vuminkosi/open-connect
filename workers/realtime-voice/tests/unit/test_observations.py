"""Unit and API tests for §4.1 concurrent observations and §7.2 adaptive endpointing."""

from __future__ import annotations

import uuid

import pytest
from fastapi.testclient import TestClient

from openconnect_realtime_voice import app as app_module
from openconnect_realtime_voice.app import app
from openconnect_realtime_voice.config import Settings
from openconnect_realtime_voice.observations import (
    EndpointEvaluator,
    ObservationRegistry,
    StaleObservationEpoch,
)

client = TestClient(app)
AUTH = {"Authorization": "Bearer local-test-token"}


def _use_settings(monkeypatch: pytest.MonkeyPatch, **overrides: object) -> None:
    params: dict[str, object] = {
        "worker_token": "local-test-token",
        "orchestration_only": True,
        "model_dir": "/models",
        "fake_engine": False,  # Real mode by default: no fake audio
        "is_production": False,
        "model_device": "auto",
    }
    params.update(overrides)
    monkeypatch.setattr(app_module, "settings", Settings(**params))
    monkeypatch.setattr(app_module, "observations", ObservationRegistry())


# --- §4.1 Concurrent observation state & gateway-epoch fencing ---------------


def test_concurrent_states_are_independent_per_tenant_and_session() -> None:
    registry = ObservationRegistry()

    snap1, ep1 = registry.observe(
        tenant_id="tenant-a",
        session_id="sess-1",
        speech_epoch=2,
        capture_id="cap-1",
        input_state="speech_detected",
        reasoning_state="generating",
        output_state="playing",
    )
    assert ep1 is None
    assert snap1.input_state == "speech_detected"
    assert snap1.reasoning_state == "generating"
    assert snap1.output_state == "playing"
    assert snap1.speech_epoch == 2
    assert snap1.capture_id == "cap-1"

    # Updating only input_state must not clobber reasoning_state or output_state.
    snap2, _ = registry.observe(
        tenant_id="tenant-a",
        session_id="sess-1",
        speech_epoch=2,
        input_state="endpoint_pending",
    )
    assert snap2.input_state == "endpoint_pending"
    assert snap2.reasoning_state == "generating"
    assert snap2.output_state == "playing"

    # Updating only output_state must not clobber input_state or reasoning_state.
    snap3, _ = registry.observe(
        tenant_id="tenant-a",
        session_id="sess-1",
        speech_epoch=2,
        output_state="draining",
    )
    assert snap3.input_state == "endpoint_pending"
    assert snap3.reasoning_state == "generating"
    assert snap3.output_state == "draining"

    # Different tenant with the same session_id is completely isolated.
    other_tenant, _ = registry.observe(
        tenant_id="tenant-b",
        session_id="sess-1",
        speech_epoch=0,
        input_state="muted",
        reasoning_state="idle",
        output_state="idle",
    )
    assert other_tenant.speech_epoch == 0
    assert other_tenant.input_state == "muted"
    assert registry.get("tenant-a", "sess-1").speech_epoch == 2
    assert registry.get("tenant-a", "sess-1").input_state == "endpoint_pending"


def test_gateway_epoch_authority_worker_never_mutates_epoch() -> None:
    registry = ObservationRegistry()

    # Initial gateway-issued epoch 4.
    snap, _ = registry.observe(
        tenant_id="tenant-a",
        session_id="sess-epoch",
        speech_epoch=4,
        capture_id="cap-4",
        input_state="speech_detected",
        output_state="interrupted",
    )
    # Even when output_state transitions to "interrupted", worker MUST NOT bump speech_epoch.
    assert snap.speech_epoch == 4

    # Stale epoch 3 is rejected and state remains untouched.
    with pytest.raises(StaleObservationEpoch) as exc_info:
        registry.observe(
            tenant_id="tenant-a",
            session_id="sess-epoch",
            speech_epoch=3,
            output_state="playing",
        )
    assert exc_info.value.expected == 4
    assert exc_info.value.presented == 3
    assert registry.get("tenant-a", "sess-epoch").output_state == "interrupted"
    assert registry.get("tenant-a", "sess-epoch").speech_epoch == 4

    # Gateway explicitly advances epoch to 5 -> worker adopts 5 and binds new capture_id.
    advanced, _ = registry.observe(
        tenant_id="tenant-a",
        session_id="sess-epoch",
        speech_epoch=5,
        capture_id="cap-5",
        input_state="listening",
        reasoning_state="idle",
        output_state="idle",
    )
    assert advanced.speech_epoch == 5
    assert advanced.capture_id == "cap-5"

    # Epoch 4 is now stale and rejected.
    with pytest.raises(StaleObservationEpoch):
        registry.observe(
            tenant_id="tenant-a",
            session_id="sess-epoch",
            speech_epoch=4,
            input_state="finalizing",
        )


# --- §7.2 Adaptive endpointing heuristics ------------------------------------


def test_hesitation_waiting_for_filler_and_ellipsis() -> None:
    evaluator = EndpointEvaluator()

    obs = evaluator.evaluate(
        silence_ms=350,
        transcript="Can you show me the... umm...",
    )
    assert obs.decision == "continue_listening"
    assert obs.recommended_silence_ms >= 700
    assert obs.authoritative is False
    assert obs.reason == "hesitation_filler_detected"
    assert 0.0 <= obs.confidence <= 1.0

    # Trailing filler without ellipsis also waits.
    obs_so = evaluator.evaluate(silence_ms=400, transcript="So...")
    assert obs_so.decision == "continue_listening"
    assert obs_so.recommended_silence_ms >= 700

    # Trailing preposition / conjunction waits.
    obs_prep = evaluator.evaluate(silence_ms=450, transcript="Show my tasks for")
    assert obs_prep.decision == "continue_listening"
    assert obs_prep.reason == "trailing_incomplete_phrase"

    # After extended hesitation timeout elapses, endpoint becomes ready.
    obs_timeout = evaluator.evaluate(
        silence_ms=1100,
        transcript="Can you show me the... umm...",
    )
    assert obs_timeout.decision == "endpoint_ready"
    assert obs_timeout.reason == "hesitation_timeout_elapsed"
    assert obs_timeout.authoritative is False


def test_clear_completed_utterance_endpoints_promptly() -> None:
    evaluator = EndpointEvaluator()

    # Too brief silence still waits.
    early = evaluator.evaluate(
        silence_ms=150,
        transcript="Can you show me the tasks due tomorrow?",
    )
    assert early.decision == "continue_listening"
    assert early.reason == "awaiting_terminal_silence"
    assert 250 <= early.recommended_silence_ms <= 400

    # Completed utterance with >= 300ms silence endpoints ready.
    ready = evaluator.evaluate(
        silence_ms=320,
        transcript="Can you show me the tasks due tomorrow?",
    )
    assert ready.decision == "endpoint_ready"
    assert ready.reason == "clear_completed_utterance"
    assert ready.confidence >= 0.9
    assert ready.authoritative is False

    # Conservative mode waits longer for the same silence duration.
    conservative = evaluator.evaluate(
        silence_ms=320,
        transcript="Can you show me the tasks due tomorrow?",
        mode="conservative",
    )
    assert conservative.decision == "continue_listening"
    assert conservative.recommended_silence_ms > ready.recommended_silence_ms


def test_backchannel_filtering_during_assistant_speech() -> None:
    evaluator = EndpointEvaluator()

    for phrase in ("mhm", "uh-huh", "yeah", "ok", "right"):
        obs = evaluator.evaluate(
            silence_ms=250,
            transcript=phrase,
            speech_duration_ms=280,
            assistant_speaking=True,
        )
        assert obs.decision == "backchannel_ignore", f"expected backchannel_ignore for {phrase!r}"
        assert obs.recommended_silence_ms == 0
        assert obs.authoritative is False
        assert obs.reason == "backchannel_during_assistant_speech"

    # Substantive interruption during assistant speech is NOT ignored as backchannel.
    interruption = evaluator.evaluate(
        silence_ms=350,
        transcript="Actually, only high priority.",
        assistant_speaking=True,
    )
    assert interruption.decision == "endpoint_ready"

    # "yeah" when assistant is NOT speaking is a real user reply, not a backchannel.
    user_reply = evaluator.evaluate(
        silence_ms=600,
        transcript="yeah",
        assistant_speaking=False,
    )
    assert user_reply.decision == "endpoint_ready"


def test_transcript_stability_and_empty_transcript() -> None:
    evaluator = EndpointEvaluator()

    # Empty transcript never endpoints.
    empty = evaluator.evaluate(silence_ms=1500, transcript="   ")
    assert empty.decision == "continue_listening"
    assert empty.reason == "empty_transcript"

    # Unstable text delays endpointing.
    unstable = evaluator.evaluate(
        silence_ms=350,
        stable_text="Show my outstanding",
        unstable_text="tasks for this week.",
    )
    assert unstable.decision == "continue_listening"
    assert unstable.reason == "transcript_unstable"

    # Active revision change with brief silence waits.
    revising = evaluator.evaluate(
        silence_ms=250,
        transcript="Show my tasks",
        transcript_revision=4,
        previous_revision=3,
        previous_transcript="Show my",
    )
    assert revising.decision == "continue_listening"
    assert revising.reason == "transcript_revision_active"


# --- HTTP API routes: /internal/v2/sessions/{id}/observe & /internal/v1/execute ---


def test_capabilities_advertise_observe_and_endpoint_without_fake_audio(monkeypatch) -> None:
    _use_settings(monkeypatch, fake_engine=False)

    ready = client.get("/health/ready").json()
    assert ready["status"] == "ready"
    assert ready["engine"] == "real"
    assert "voice.observe" in ready["capabilities"]
    assert "voice.endpoint.evaluate" in ready["capabilities"]
    assert "voice.fake-chunks" not in ready["capabilities"]

    caps = client.get("/internal/v1/capabilities", headers=AUTH).json()
    assert "voice.observe" in caps["capabilities"]
    assert "voice.endpoint.evaluate" in caps["capabilities"]
    assert "voice.observe" in caps["executeKinds"]
    assert "voice.endpoint.evaluate" in caps["executeKinds"]
    assert "voice.fake-chunks" not in caps["capabilities"]


def test_v2_observe_route_auth_concurrent_state_and_stale_epoch(monkeypatch) -> None:
    _use_settings(monkeypatch, fake_engine=False)
    sid = str(uuid.uuid4())
    url = f"/internal/v2/sessions/{sid}/observe"

    # Bearer authentication required.
    assert client.post(url, json={"tenantId": "t1", "speechEpoch": 1}).status_code == 401
    assert (
        client.post(
            url,
            json={"tenantId": "t1", "speechEpoch": 1},
            headers={"Authorization": "Bearer wrong"},
        ).status_code
        == 401
    )

    # Mismatched body sessionId rejected.
    mismatch = client.post(
        url,
        json={"tenantId": "t1", "sessionId": "other-session", "speechEpoch": 1},
        headers=AUTH,
    )
    assert mismatch.status_code == 422

    # Valid observation with hesitation returns continue_listening and authoritative=False.
    res1 = client.post(
        url,
        json={
            "tenantId": "t1",
            "speechEpoch": 2,
            "captureId": "cap-2",
            "inputState": "speech_detected",
            "reasoningState": "idle",
            "outputState": "idle",
            "silenceMs": 350,
            "transcript": "Can you show me the... umm...",
            "transcriptRevision": 1,
        },
        headers=AUTH,
    )
    assert res1.status_code == 200
    body1 = res1.json()
    assert body1["schemaVersion"] == "openconnect.v2"
    assert body1["authoritative"] is False
    assert body1["observation"]["speechEpoch"] == 2
    assert body1["observation"]["captureId"] == "cap-2"
    assert body1["observation"]["inputState"] == "speech_detected"
    assert body1["endpointObservation"]["decision"] == "continue_listening"
    assert body1["endpointObservation"]["authoritative"] is False
    assert "chunks" not in body1

    # Completed utterance at same epoch endpoints ready.
    res2 = client.post(
        url,
        json={
            "tenantId": "t1",
            "speechEpoch": 2,
            "captureId": "cap-2",
            "inputState": "endpoint_pending",
            "silenceMs": 350,
            "transcript": "Can you show me the tasks due tomorrow?",
            "transcriptRevision": 2,
        },
        headers=AUTH,
    )
    assert res2.status_code == 200
    body2 = res2.json()
    assert body2["endpointObservation"]["decision"] == "endpoint_ready"
    assert body2["observation"]["speechEpoch"] == 2

    # Stale epoch 1 rejected with 409.
    stale = client.post(
        url,
        json={"tenantId": "t1", "speechEpoch": 1, "inputState": "listening"},
        headers=AUTH,
    )
    assert stale.status_code == 409
    assert "stale speech epoch" in stale.json()["detail"]


def test_execute_voice_observe_and_endpoint_evaluate_in_real_mode(monkeypatch) -> None:
    _use_settings(monkeypatch, fake_engine=False)
    sid = str(uuid.uuid4())

    # voice.observe sets assistant output_state="playing".
    obs_resp = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "task-obs-1",
            "kind": "voice.observe",
            "payload": {
                "schemaVersion": "openconnect.v2",
                "tenantId": "tenant-1",
                "sessionId": sid,
                "speechEpoch": 3,
                "captureId": "cap-3",
                "inputState": "listening",
                "reasoningState": "completed",
                "outputState": "playing",
            },
        },
        headers=AUTH,
    )
    assert obs_resp.status_code == 200
    obs_data = obs_resp.json()["result"]
    assert obs_data["authoritative"] is False
    assert obs_data["observation"]["outputState"] == "playing"
    assert obs_data["observation"]["speechEpoch"] == 3

    # voice.endpoint.evaluate infers assistant_speaking=True from outputState="playing"
    # and filters "mhm" as a backchannel.
    eval_resp = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "task-eval-1",
            "kind": "voice.endpoint.evaluate",
            "payload": {
                "schemaVersion": "openconnect.v2",
                "tenantId": "tenant-1",
                "sessionId": sid,
                "speechEpoch": 3,
                "captureId": "cap-3",
                "silenceMs": 200,
                "speechDurationMs": 250,
                "transcript": "mhm",
            },
        },
        headers=AUTH,
    )
    assert eval_resp.status_code == 200
    eval_data = eval_resp.json()["result"]
    assert eval_data["authoritative"] is False
    ep = eval_data["endpointObservation"]
    assert ep["decision"] == "backchannel_ignore"
    assert ep["recommendedSilenceMs"] == 0
    assert ep["authoritative"] is False
    assert "chunks" not in eval_data

    # Stale epoch on voice.endpoint.evaluate returns 409.
    stale_eval = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "task-eval-stale",
            "kind": "voice.endpoint.evaluate",
            "payload": {
                "schemaVersion": "openconnect.v2",
                "tenantId": "tenant-1",
                "sessionId": sid,
                "speechEpoch": 2,
                "silenceMs": 400,
                "transcript": "Hello.",
            },
        },
        headers=AUTH,
    )
    assert stale_eval.status_code == 409


def test_task_claim_supports_new_execute_kinds(monkeypatch) -> None:
    _use_settings(monkeypatch, fake_engine=False)
    for idx, kind in enumerate(("voice.observe", "voice.endpoint.evaluate")):
        resp = client.post(
            "/internal/v1/tasks/claim",
            json={"taskId": f"v2-claim-{idx}-{uuid.uuid4().hex[:6]}", "kind": kind},
            headers=AUTH,
        )
        assert resp.status_code == 200
        assert resp.json()["kind"] == kind


def test_v1_regression_unchanged_alongside_v2_observations(monkeypatch) -> None:
    _use_settings(monkeypatch, fake_engine=True)
    sid = str(uuid.uuid4())

    opened = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-v1-open",
            "kind": "voice.session-open",
            "payload": {"schemaVersion": "openconnect.v1", "sessionId": sid},
        },
        headers=AUTH,
    )
    assert opened.status_code == 200
    assert opened.json()["state"] == "idle"

    # Interleave a v2 observation at gateway epoch 7; v1 session state remains isolated.
    v2 = client.post(
        f"/internal/v2/sessions/{sid}/observe",
        json={
            "tenantId": "tenant-v1-compat",
            "speechEpoch": 7,
            "inputState": "listening",
        },
        headers=AUTH,
    )
    assert v2.status_code == 200

    v1_state = client.get(f"/internal/v1/voice/sessions/{sid}", headers=AUTH).json()
    assert v1_state["state"] == "idle"
    assert v1_state["speechEpoch"] == 0
