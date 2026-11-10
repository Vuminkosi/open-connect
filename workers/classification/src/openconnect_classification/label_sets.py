"""Versioned classification label sets.

Label sets are versioned explicitly: requests pin `labelSetVersion` and the
worker rejects unknown versions instead of silently substituting. New labels
are added in new versions; existing versions are append-only history.
"""

LABEL_SETS: dict[str, tuple[str, ...]] = {
    "v1": ("safe", "spam", "toxic", "sensitive"),
    "v2": ("safe", "spam", "toxic", "sensitive", "pii", "injection"),
}

DEFAULT_LABEL_SET_VERSION = "v1"


def label_set_versions() -> list[str]:
    return sorted(LABEL_SETS)


def get_label_set(version: str) -> tuple[str, ...]:
    try:
        return LABEL_SETS[version]
    except KeyError:
        raise KeyError(f"unknown label set version: {version}") from None
