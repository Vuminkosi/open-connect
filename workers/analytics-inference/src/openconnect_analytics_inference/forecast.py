"""Deterministic seasonal-naive forecasts with chronological holdout errors."""

from __future__ import annotations

import math
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from openconnect_analytics_inference.statistics import MAX_SAMPLES

MAX_SEASON = 365
MAX_HORIZON = 100
MAX_INTERVAL_SECONDS = 365 * 24 * 60 * 60


@dataclass(frozen=True, repr=False)
class ForecastPoint:
    timestamp: str
    value: float
    lower: float
    upper: float


@dataclass(frozen=True, repr=False)
class Backtest:
    samples: int
    mean_absolute_error: float
    naive_mean_absolute_error: float
    residual_lower: float
    residual_upper: float


@dataclass(frozen=True, repr=False)
class Forecast:
    algorithm_version: str
    interval_seconds: int
    seasonal_period: int
    sample_size: int
    observed_count: int
    coverage: float
    points: tuple[ForecastPoint, ...]
    backtest: Backtest | None
    warnings: tuple[str, ...]


def forecast(values: Sequence[float | None], timestamps: Sequence[datetime], *,
             interval_seconds: int, seasonal_period: int, horizon: int) -> Forecast:
    """Forecast regular series; residual bands are empirical, not calibrated confidence intervals."""
    if len(values) > MAX_SAMPLES or len(values) != len(timestamps):
        raise ValueError("invalid_series")
    for value, upper in ((interval_seconds, MAX_INTERVAL_SECONDS),
                         (seasonal_period, MAX_SEASON), (horizon, MAX_HORIZON)):
        if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= upper:
            raise ValueError("invalid_forecast_configuration")
    numbers: list[float | None] = []
    for value in values:
        if value is None:
            numbers.append(None)
            continue
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise ValueError("invalid_value")
        try:
            number = float(value)
        except OverflowError as exc:
            raise ValueError("invalid_value") from exc
        if not math.isfinite(number) or abs(number) > 1e150:
            raise ValueError("invalid_value")
        numbers.append(number)
    times: list[datetime] = []
    for timestamp in timestamps:
        if not isinstance(timestamp, datetime) or timestamp.tzinfo is None or timestamp.utcoffset() is None:
            raise ValueError("invalid_timestamp")
        try:
            times.append(timestamp.astimezone(timezone.utc))
        except (OverflowError, ValueError) as exc:
            raise ValueError("invalid_timestamp") from exc
    observed = sum(value is not None for value in numbers)
    common = dict(algorithm_version="seasonal-naive-v1", interval_seconds=interval_seconds,
                  seasonal_period=seasonal_period, sample_size=len(numbers), observed_count=observed,
                  coverage=observed / len(numbers) if numbers else 0.0)
    warnings: list[str] = []
    if any(later - earlier != timedelta(seconds=interval_seconds)
           for earlier, later in zip(times, times[1:])):
        warnings.append("irregular_timestamps")
    if observed != len(numbers):
        warnings.append("missing_values")
    holdout = max(seasonal_period, 5)
    if len(numbers) < 2 * seasonal_period + holdout:
        warnings.append("insufficient_history")
    if warnings:
        return Forecast(**common, points=(), backtest=None, warnings=tuple(warnings))

    # Reject the entire result if any forecast timestamp is outside the supported
    # datetime range. Do this before building points to avoid a partial forecast.
    try:
        times[-1] + timedelta(seconds=interval_seconds * horizon)
    except OverflowError as exc:
        raise ValueError("invalid_timestamp") from exc

    series = [float(value) for value in numbers if value is not None]
    start = len(series) - holdout
    residuals = [series[i] - series[i - seasonal_period] for i in range(start, len(series))]
    naive_errors = [abs(series[i] - series[i - 1]) for i in range(start, len(series))]
    mae = math.fsum(abs(value) for value in residuals) / holdout
    naive_mae = math.fsum(naive_errors) / holdout
    ordered = sorted(residuals)
    lower = ordered[max(0, math.ceil(0.05 * holdout) - 1)]
    upper = ordered[math.ceil(0.95 * holdout) - 1]
    backtest = Backtest(holdout, mae, naive_mae, lower, upper)
    warnings.append("empirical_bands_not_calibrated")
    if horizon > 1:
        warnings.append("multi_step_uncertainty_not_estimated")
    if holdout < 20:
        warnings.append("limited_backtest_samples")
    if seasonal_period > 1 and mae >= naive_mae:
        warnings.append("seasonality_not_improved_over_naive")
    points = []
    for index in range(horizon):
        predicted = series[-seasonal_period + index % seasonal_period]
        timestamp = times[-1] + timedelta(seconds=interval_seconds * (index + 1))
        points.append(ForecastPoint(timestamp.isoformat(), predicted, predicted + lower, predicted + upper))
    return Forecast(**common, points=tuple(points), backtest=backtest, warnings=tuple(warnings))
