"""Generate deterministic Python analytics results for the .NET contract tests."""

from dataclasses import asdict
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))

def fixture(values: list[float | None], *, season: int, horizon: int) -> dict:
    from openconnect_analytics_inference.forecast import forecast
    from openconnect_analytics_inference.statistics import analyze
    from openconnect_analytics_inference.trend import trend

    times = [datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(days=index)
             for index in range(len(values))]
    settings = {"timestamps": [value.isoformat() for value in times], "intervalSeconds": 86400,
                "seasonalPeriod": season, "horizon": horizon}
    payload = {"feature": "weight", "unit": "kg", "lineage": "fixture", "values": values,
               "forecast": settings, "trend": True}
    result = {"feature": "weight", "unit": "kg", "lineage": "fixture", **asdict(analyze(values)),
              "forecast": asdict(forecast(values, times, interval_seconds=86400,
                                           seasonal_period=season, horizon=horizon)),
              "trend": asdict(trend(values))}
    return {"payload": payload, "response": {"schemaVersion": "openconnect.v1", "taskId": "fixture",
                                               "kind": "analytics.analyze", "result": result}}


def main() -> None:
    cases = {"regular": fixture([1, 2, 3, 4, 5, 6, 7, 8, 9], season=2, horizon=3),
             "missing": fixture([1, None, 3], season=1, horizon=2),
             "empty": fixture([], season=1, horizon=1)}
    target = Path(__file__).resolve().parents[4] / "tests" / "Unit" / "OpenConnect.Analytics.Tests" / "Fixtures" / "python-analytics.json"
    content = json.dumps(cases, indent=2, sort_keys=True, allow_nan=False) + "\n"
    if "--check" in sys.argv:
        if target.read_text(encoding="utf-8") != content:
            raise SystemExit("analytics fixture is stale")
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
