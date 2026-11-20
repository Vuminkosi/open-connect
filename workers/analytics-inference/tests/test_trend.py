import pytest

from openconnect_analytics_inference.trend import trend


def test_slope_uses_original_positions_when_observations_are_missing():
    result = trend([2, None, 6, 8])
    assert result.slope_per_sample == pytest.approx(2)
    assert result.sample_size == 4 and result.observed_count == 3
    assert "missing_values" in result.warnings
    assert "descriptive_only_no_significance_test" in result.warnings


@pytest.mark.parametrize(("values", "slope"), [([4, 4, 4], 0), ([6, 4, 2], -2)])
def test_constant_and_falling_series(values, slope):
    assert trend(values).slope_per_sample == pytest.approx(slope)


def test_insufficient_data_does_not_produce_slope():
    result = trend([1, None, 3])
    assert result.slope_per_sample is None and "insufficient_history" in result.warnings


@pytest.mark.parametrize("value", [True, float("inf"), float("nan"), 10**1000, "1"])
def test_invalid_values_are_rejected(value):
    with pytest.raises(ValueError, match="invalid_value"):
        trend([1, value, 2])


def test_sample_limit_is_enforced():
    with pytest.raises(ValueError, match="sample_limit_exceeded"):
        trend([1] * 10_001)
