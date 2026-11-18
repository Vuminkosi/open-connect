"""Explicit NLLB language-pair allowlist (Flores-200 codes).

Pairs are allowlisted explicitly: any source/target combination not present
here is rejected with `language_unsupported`. Adding a pair is a deliberate
change to this table, never an implicit fallback.
"""

SUPPORTED_LANGUAGES: tuple[str, ...] = (
    "eng_Latn",
    "fra_Latn",
    "deu_Latn",
    "spa_Latn",
    "por_Latn",
    "ita_Latn",
    "nld_Latn",
    "pol_Latn",
    "ukr_Cyrl",
    "rus_Cyrl",
    "ara_Arab",
    "hin_Deva",
    "ben_Beng",
    "swh_Latn",
    "hau_Latn",
    "yor_Latn",
    "zul_Latn",
    "amh_Ethi",
)

SUPPORTED_PAIRS: frozenset[tuple[str, str]] = frozenset(
    {
        ("eng_Latn", "fra_Latn"),
        ("fra_Latn", "eng_Latn"),
        ("eng_Latn", "deu_Latn"),
        ("deu_Latn", "eng_Latn"),
        ("eng_Latn", "spa_Latn"),
        ("spa_Latn", "eng_Latn"),
        ("eng_Latn", "por_Latn"),
        ("eng_Latn", "ita_Latn"),
        ("eng_Latn", "nld_Latn"),
        ("eng_Latn", "pol_Latn"),
        ("eng_Latn", "ukr_Cyrl"),
        ("eng_Latn", "rus_Cyrl"),
        ("eng_Latn", "ara_Arab"),
        ("eng_Latn", "hin_Deva"),
        ("eng_Latn", "ben_Beng"),
        ("eng_Latn", "swh_Latn"),
        ("swh_Latn", "eng_Latn"),
        ("eng_Latn", "hau_Latn"),
        ("hau_Latn", "eng_Latn"),
        ("eng_Latn", "yor_Latn"),
        ("yor_Latn", "eng_Latn"),
        ("eng_Latn", "zul_Latn"),
        ("zul_Latn", "eng_Latn"),
        ("eng_Latn", "amh_Ethi"),
        ("amh_Ethi", "eng_Latn"),
        ("fra_Latn", "swh_Latn"),
        ("swh_Latn", "fra_Latn"),
    }
)


def is_supported_pair(source: str, target: str) -> bool:
    return (source, target) in SUPPORTED_PAIRS
