from datetime import datetime, timedelta, timezone

import pytest

from openconnect_analytics_inference.forecast import forecast


def times(count: int) -> list[datetime]:
    start = datetime(2026, 1, 1, tzinfo=timezone.utc)
    return [start + timedelta(days=index) for index in range(count)]


def run(values, *, seasonal_period=1, horizon=3, timestamps=None):
    return forecast(values, times(len(values)) if timestamps is None else timestamps,
                    interval_seconds=86400, seasonal_period=seasonal_period, horizon=horizon)


def test_seasonal_forecast_backtests_against_held_out_observations():
    result = run([10, 20, 30] * 5, seasonal_period=3, horizon=5)
    assert [point.value for point in result.points] == [10, 20, 30, 10, 20]
    assert result.backtest.samples == 5
    assert result.backtest.mean_absolute_error == 0
    assert result.backtest.naive_mean_absolute_error > 0
    assert all(point.lower == point.value == point.upper for point in result.points)
    assert result.points[0].timestamp == times(16)[-1].isoformat()
    assert "empirical_bands_not_calibrated" in result.warnings
    assert "multi_step_uncertainty_not_estimated" in result.warnings


def test_holdout_residual_bands_are_explicit_and_deterministic():
    result = run([1, 2, 3, 4, 5, 6, 7, 9])
    assert result.backtest.mean_absolute_error == pytest.approx(1.2)
    assert result.points[0].value == 9
    assert (result.points[0].lower, result.points[0].upper) == (10, 11)
    assert result == run([1, 2, 3, 4, 5, 6, 7, 9])
    assert "multi_step_uncertainty_not_estimated" not in run([1, 2, 3, 4, 5, 6, 7, 9], horizon=1).warnings


@pytest.mark.parametrize("values,warning", [([], "insufficient_history"),
    ([1, 2, None, 4, 5, 6, 7], "missing_values"), ([1, 2, 3], "insufficient_history")])
def test_incomplete_data_never_produces_forecasts(values, warning):
    result = run(values)
    assert result.points == () and result.backtest is None and warning in result.warnings


def test_irregular_timestamps_do_not_produce_forecasts():
    stamps = times(10)
    stamps[-1] += timedelta(days=1)
    result = run(list(range(10)), timestamps=stamps)
    assert result.points == () and "irregular_timestamps" in result.warnings


def test_constant_series_reports_no_claim_of_seasonality():
    result = run([4] * 15, seasonal_period=3)
    assert result.backtest.mean_absolute_error == 0
    assert "seasonality_not_improved_over_naive" in result.warnings


@pytest.mark.parametrize("options", [{"interval_seconds": 0}, {"seasonal_period": 0},
                                     {"horizon": 101}, {"horizon": True}])
def test_invalid_limits_are_rejected(options):
    config = {"interval_seconds": 86400, "seasonal_period": 1, "horizon": 1} | options
    with pytest.raises(ValueError):
        forecast([1], times(1), **config)


def test_mixed_time_zones_use_elapsed_utc_time():
    stamps = [stamp.astimezone(timezone(timedelta(hours=2))) for stamp in times(8)]
    assert len(run(list(range(8)), timestamps=stamps).points) == 3
