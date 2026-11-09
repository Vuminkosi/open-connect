import pytest

from openconnect_realtime_voice.sessions import (
    InvalidTransition,
    SessionRegistry,
    StaleEpoch,
)


def test_epoch_fencing_discards_stale_without_applying() -> None:
    registry = SessionRegistry()
    registry.open("s1")
    registry.apply_event("s1", "begin-listening")
    registry.apply_event("s1", "transcript-final", text="hi")
    registry.apply_event("s1", "begin-reasoning")
    session, _ = registry.apply_event("s1", "begin-speaking", speech_epoch=0)
    assert session.state == "speaking"

    # Wrong epoch while still speaking is fenced and discarded, state untouched.
    with pytest.raises(StaleEpoch):
        registry.apply_event("s1", "utterance-complete", speech_epoch=99)
    assert registry.get("s1").state == "speaking"

    session, detail = registry.apply_event("s1", "barge-in")
    assert session.state == "interrupted"
    assert detail == {"discardedEpoch": 0, "speechEpoch": 1}

    # Stale-epoch completion after barge-in is discarded, never applied.
    with pytest.raises(InvalidTransition):
        registry.apply_event("s1", "utterance-complete", speech_epoch=0)
    assert registry.get("s1").state == "interrupted"
    assert registry.get("s1").speech_epoch == 1


def test_terminal_states_reject_further_events() -> None:
    registry = SessionRegistry()
    registry.open("s2")
    registry.apply_event("s2", "fail")
    assert registry.get("s2").state == "failed"
    with pytest.raises(InvalidTransition):
        registry.apply_event("s2", "begin-listening")

    registry.open("s3")
    registry.apply_event("s3", "cancel")
    assert registry.get("s3").state == "cancelled"
    with pytest.raises(InvalidTransition):
        registry.apply_event("s3", "barge-in")


def test_unknown_event_rejected() -> None:
    registry = SessionRegistry()
    registry.open("s4")
    with pytest.raises(InvalidTransition):
        registry.apply_event("s4", "teleport")
