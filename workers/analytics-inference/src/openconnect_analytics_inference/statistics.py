"""Descriptive statistics and robust anomaly screening without trained models."""

from __future__ import annotations

import math
from collections.abc import Sequence
from dataclasses import dataclass
from statistics import median

MAX_SAMPLES = 10_000
MIN_ANOMALY_SAMPLES = 5
ALGORITHM_VERSION = "robust-mad-v1"


@dataclass(frozen=True)
class Anomaly:
    index: int
    value: float
    robust_score: float


@dataclass(frozen=True)
class Analysis:
    sample_size: int
    observed_count: int
    missing_count: int
    coverage: float
    minimum: float | None
    maximum: float | None
    median: float | None
    median_absolute_deviation: float | None
    threshold: float
    anomalies: tuple[Anomaly, ...]
    warnings: tuple[str, ...]
    algorithm_version: str = ALGORITHM_VERSION


def analyze(values: Sequence[float | None], *, threshold: float = 3.5) -> Analysis:
    """Summarize one homogeneous feature and flag robust median deviations.

    Missing entries retain their input position. A zero MAD or insufficient
    baseline yields no scores rather than an invented confidence estimate.
    Callers must validate the feature's unit, tenant scope, and provenance.
    """
    if len(values) > MAX_SAMPLES:
        raise ValueError("sample_limit_exceeded")
    if isinstance(threshold, bool) or not isinstance(threshold, (int, float)):
        raise ValueError("invalid_threshold")
    if not math.isfinite(threshold) or threshold <= 0 or threshold > 100:
        raise ValueError("invalid_threshold")

    observed: list[tuple[int, float]] = []
    for index, value in enumerate(values):
        if value is None:
            continue
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise ValueError("invalid_value")
        try:
            number = float(value)
        except OverflowError as exc:
            raise ValueError("invalid_value") from exc
        if not math.isfinite(number) or abs(number) > 1e150:
            raise ValueError("invalid_value")
        observed.append((index, number))

    numbers = [value for _, value in observed]
    count = len(numbers)
    center = median(numbers) if numbers else None
    mad = median([abs(value - center) for value in numbers]) if center is not None else None
    warnings: list[str] = []
    anomalies: list[Anomaly] = []
    if count < MIN_ANOMALY_SAMPLES:
        warnings.append("insufficient_baseline")
    elif mad == 0:
        warnings.append("zero_median_absolute_deviation")
    else:
        assert center is not None and mad is not None
        for index, value in observed:
            score = 0.6744897501960817 * ((value - center) / mad)
            if not math.isfinite(score):
                raise ValueError("numeric_range_exceeded")
            if abs(score) > threshold:
                anomalies.append(Anomaly(index=index, value=value, robust_score=score))

    return Analysis(
        sample_size=len(values),
        observed_count=count,
        missing_count=len(values) - count,
        coverage=count / len(values) if values else 0.0,
        minimum=min(numbers) if numbers else None,
        maximum=max(numbers) if numbers else None,
        median=center,
        median_absolute_deviation=mad,
        threshold=float(threshold),
        anomalies=tuple(anomalies),
        warnings=tuple(warnings),
    )
