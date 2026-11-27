# Pin this major-minor image to its registry digest in release builds.
FROM python:3.12-slim AS base

COPY --from=ghcr.io/astral-sh/uv:0.7.12 /uv /uvx /bin/
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    OC_MODEL_PROVISIONING=disabled

WORKDIR /app/workers/imessaging

FROM base AS dependencies
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/imessaging/pyproject.toml workers/imessaging/uv.lock ./
RUN uv sync --locked --no-dev --no-install-project

FROM base AS runtime
ENV PATH="/app/workers/imessaging/.venv/bin:$PATH" \
    PYTHONPATH=/app/workers/imessaging/src \
    OC_IMESSAGING_ENABLED=false \
    OC_IMESSAGING_PUSH_MODE=disabled \
    OC_IMESSAGING_RELAY_ENABLED=false \
    OC_MODEL_PROVISIONING=disabled
COPY --from=dependencies /app/workers/imessaging/.venv ./.venv
COPY workers/imessaging/src/ ./src/
RUN chown -R 10001:10001 /app
USER 10001:10001

EXPOSE 8081
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8081/health/live', timeout=2)"]

CMD ["uvicorn", "openconnect_imessaging.api.app:app", "--host", "0.0.0.0", "--port", "8081", "--limit-concurrency", "64"]
