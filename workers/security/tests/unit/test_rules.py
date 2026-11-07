from openconnect_security.detectors.rules import scan


def test_detects_email_phone_and_secret_with_offsets() -> None:
    text = "Email a.person@example.org phone +1-202-555-0100 api_key=abcd1234"
    spans = scan(text)
    assert {span.entityType for span in spans} == {"EMAIL", "PHONE", "SECRET"}
    assert all(text[span.start : span.end] for span in spans)
    assert all(span.decision == "mask" for span in spans)


def test_no_match_returns_empty() -> None:
    assert scan("nothing sensitive") == []
