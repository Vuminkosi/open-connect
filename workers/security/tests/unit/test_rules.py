"""Deterministic rules detector unit tests (synthetic positive and negative fixtures)."""

from __future__ import annotations

from openconnect_security.detectors.rules import is_valid_payment_card, is_valid_sa_id, scan


def _make_luhn(prefix: str) -> str:
    """Append a valid Luhn check digit to a synthetic digit prefix."""
    digits = [int(ch) for ch in prefix]
    total = 0
    # When check digit is appended, rightmost of prefix is at index -2 (doubled).
    for idx, d in enumerate(reversed(digits)):
        if idx % 2 == 0:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    check = (10 - (total % 10)) % 10
    return f"{prefix}{check}"


def test_detects_email_phone_and_secret_with_offsets() -> None:
    text = "Email a.person@example.org phone +1-202-555-0100 api_key=abcd1234"
    spans = scan(text)
    assert {span.entityType for span in spans} == {"EMAIL", "PHONE", "SECRET"}
    assert all(text[span.start : span.end] for span in spans)
    assert all(span.decision == "mask" for span in spans)


def test_no_match_returns_empty() -> None:
    assert scan("nothing sensitive") == []


def test_payment_card_luhn_positive_and_negative() -> None:
    valid_card = _make_luhn("411111111111111")  # 16 digits
    formatted = f"{valid_card[0:4]}-{valid_card[4:8]}-{valid_card[8:12]}-{valid_card[12:16]}"
    invalid_card = f"{valid_card[:-1]}{(int(valid_card[-1]) + 1) % 10}"

    assert is_valid_payment_card(formatted)
    assert not is_valid_payment_card(invalid_card)
    assert not is_valid_payment_card("0000-0000-0000-0000")

    text = f"Card {formatted} and bad {invalid_card}"
    spans = scan(text)
    cards = [s for s in spans if s.entityType == "PAYMENT_CARD"]
    assert len(cards) == 1
    assert text[cards[0].start : cards[0].end] == formatted
    # Valid payment card must not also be duplicated as a PHONE span.
    assert not any(
        s.entityType == "PHONE" and s.start == cards[0].start and s.end == cards[0].end
        for s in spans
    )


def test_south_african_id_positive_and_negative() -> None:
    valid_sa_id = _make_luhn("900101500908")  # 1990-01-01, citizen=0, valid Luhn
    bad_date = _make_luhn("900230500908")  # Feb 30 impossible
    bad_citizen = _make_luhn("900101500928")  # citizenship digit 2 invalid
    bad_luhn = f"{valid_sa_id[:-1]}{(int(valid_sa_id[-1]) + 1) % 10}"

    assert is_valid_sa_id(valid_sa_id)
    assert not is_valid_sa_id(bad_date)
    assert not is_valid_sa_id(bad_citizen)
    assert not is_valid_sa_id(bad_luhn)

    text = f"SA ID {valid_sa_id} invalid {bad_date} {bad_citizen} {bad_luhn}"
    spans = scan(text)
    gov_ids = [s for s in spans if s.entityType == "GOVERNMENT_ID"]
    assert len(gov_ids) == 1
    assert text[gov_ids[0].start : gov_ids[0].end] == valid_sa_id
    # Valid SA ID must not also emit a spurious PHONE span.
    assert not any(
        s.entityType == "PHONE" and s.start == gov_ids[0].start and s.end == gov_ids[0].end
        for s in spans
    )


def test_contextual_passport_account_and_tax_identifiers() -> None:
    text = (
        "Passport No: A12345678 | "
        "Account Number: ZA9912345678 | "
        "SARS Ref: 9876543210"
    )
    spans = scan(text)
    by_type = {(s.entityType, text[s.start : s.end]) for s in spans}
    assert ("GOVERNMENT_ID", "A12345678") in by_type
    assert ("ACCOUNT_NUMBER", "ZA9912345678") in by_type
    assert ("GOVERNMENT_ID", "9876543210") in by_type

    # Bare alphanumeric/numeric tokens without context must not falsely claim passport/account/tax.
    benign = "Order reference A12345678 batch 9876543210"
    benign_spans = scan(benign)
    assert not any(s.entityType in {"GOVERNMENT_ID", "ACCOUNT_NUMBER"} for s in benign_spans)


def test_expanded_secret_patterns() -> None:
    fake_openai = "sk-proj-abcdefghijklmnopqrstuvwxyz123456"
    fake_gh = "ghp_1234567890abcdefghij1234567890abcd"
    fake_aws = "AKIAIOSFODNN7EXAMPLE"
    text = f"Keys: {fake_openai} {fake_gh} {fake_aws}"
    spans = scan(text)
    secrets = {text[s.start : s.end] for s in spans if s.entityType == "SECRET"}
    assert fake_openai in secrets
    assert fake_gh in secrets
    assert fake_aws in secrets


def test_unicode_scalar_offsets_with_emoji_and_multibyte_chars() -> None:
    text = "Hello 🌍 café — contact demo@example.org!"
    spans = scan(text)
    assert len(spans) == 1
    span = spans[0]
    assert span.entityType == "EMAIL"
    assert text[span.start : span.end] == "demo@example.org"
