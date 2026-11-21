import math

import pytest

from openconnect_analytics_inference.forecast import Backtest, Forecast, ForecastPoint
from openconnect_analytics_inference.statistics import MAX_SAMPLES, Anomaly, analyze
from openconnect_analytics_inference.trend import Trend


def test_analytics_result_representations_do_not_expose_customer_values() -> None:
    value = 123456.789
    anomaly = Anomaly(1, value, 2.0)
    analysis = analyze([value])
    trend = Trend("v1", 1, 1, value, ())
    point = ForecastPoint("2026-01-01T00:00:00Z", value, value, value)
    backtest = Backtest(1, value, value, value, value)
    result = Forecast("v1", 60, 2, 2, 2, 1.0, (point,), backtest, ())
    for item in (anomaly, analysis, trend, point, backtest, result):
        assert str(value) not in repr(item)
    assert point.timestamp not in repr(point)


def test_summary_and_anomaly_preserve_source_position() -> None:
    result = analyze([1, None, 2, 3, 4, 5, 100])
    assert (result.sample_size, result.observed_count, result.missing_count) == (7, 6, 1)
    assert result.coverage == pytest.approx(6 / 7)
    assert result.median == 3.5
    assert [(item.index, item.value) for item in result.anomalies] == [(6, 100)]
    assert result.algorithm_version == "robust-mad-v1"


@pytest.mark.parametrize("values", [[], [None], [1, 2, 3, 100]])
def test_insufficient_baseline_never_claims_anomalies(values: list[float | None]) -> None:
    result = analyze(values)
    assert result.anomalies == ()
    assert result.warnings == ("insufficient_baseline",)


def test_zero_mad_does_not_invent_scores() -> None:
    result = analyze([1, 1, 1, 1, 100])
    assert result.anomalies == ()
    assert result.warnings == ("zero_median_absolute_deviation",)


@pytest.mark.parametrize("value", [math.inf, -math.inf, math.nan, True, "1", 10**400])
def test_invalid_values_are_rejected(value: object) -> None:
    with pytest.raises(ValueError, match="invalid_value"):
        analyze([value])


@pytest.mark.parametrize("threshold", [0, -1, math.nan, math.inf, 101, True])
def test_invalid_threshold_is_rejected(threshold: float) -> None:
    with pytest.raises(ValueError, match="invalid_threshold"):
        analyze([1], threshold=threshold)


def test_sample_count_is_bounded() -> None:
    with pytest.raises(ValueError, match="sample_limit_exceeded"):
        analyze([1.0] * (MAX_SAMPLES + 1))
