"""Descriptive linear trend over evenly spaced sample positions."""

from __future__ import annotations

import math
from collections.abc import Sequence
from dataclasses import dataclass

from openconnect_analytics_inference.statistics import MAX_SAMPLES


@dataclass(frozen=True)
class Trend:
    algorithm_version: str
    sample_size: int
    observed_count: int
    slope_per_sample: float | None
    warnings: tuple[str, ...]


def trend(values: Sequence[float | None]) -> Trend:
    """Fit a descriptive slope against positions; do not infer causality or significance."""
    if len(values) > MAX_SAMPLES:
        raise ValueError("sample_limit_exceeded")
    observed: list[tuple[int, float]] = []
    for index, value in enumerate(values):
        if value is None:
            continue
        if isinstance(value, bool) or not isinstance(value, (float, int)):
            raise ValueError("invalid_value")
        try:
            number = float(value)
        except OverflowError as exc:
            raise ValueError("invalid_value") from exc
        if not math.isfinite(number) or abs(number) > 1e150:
            raise ValueError("invalid_value")
        observed.append((index, number))

    warnings = ["descriptive_only_no_significance_test"]
    if len(observed) != len(values):
        warnings.append("missing_values")
    slope = None
    if len(observed) < 3:
        warnings.append("insufficient_history")
    else:
        mean_x = math.fsum(index for index, _ in observed) / len(observed)
        mean_y = math.fsum(value for _, value in observed) / len(observed)
        denominator = math.fsum((index - mean_x) ** 2 for index, _ in observed)
        slope = math.fsum((index - mean_x) * (value - mean_y) for index, value in observed) / denominator
    return Trend("linear-position-v1", len(values), len(observed), slope, tuple(warnings))
