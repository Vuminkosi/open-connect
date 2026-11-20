FROM python:3.12-slim AS base
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /uvx /bin/
ENV UV_LINK_MODE=copy PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app/workers/analytics-inference

FROM base AS dependencies
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/analytics-inference/pyproject.toml workers/analytics-inference/uv.lock ./
RUN uv sync --locked --no-dev --no-install-project

FROM base AS runtime
ENV PATH="/app/workers/analytics-inference/.venv/bin:$PATH" \
    PYTHONPATH=/app/workers/analytics-inference/src \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
COPY --from=dependencies /app/workers/analytics-inference/.venv ./.venv
COPY workers/analytics-inference/src/ ./src/
USER 10001:10001
EXPOSE 8080
CMD ["uvicorn", "openconnect_analytics_inference.app:app", "--host", "0.0.0.0", "--port", "8080", "--limit-concurrency", "16"]
