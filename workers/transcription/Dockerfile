# Pin this major-minor image to its registry digest in release builds.
FROM python:3.12-slim AS base

COPY --from=ghcr.io/astral-sh/uv:0.7.12 /uv /uvx /bin/
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    # Offline by default: this worker must never download models at runtime.
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HOME=/models

WORKDIR /app

FROM base AS dependencies
COPY pyproject.toml uv.lock ./
RUN uv sync --locked --no-dev --no-install-project

FROM base AS runtime
ENV PATH="/app/.venv/bin:$PATH" \
    PYTHONPATH=/app/src \
    MODEL_DIR=/models
COPY --from=dependencies /app/.venv /app/.venv
COPY src/ /app/src/
RUN mkdir -p /models && chown -R 10001:10001 /app /models
USER 10001:10001

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health/ready', timeout=2)"]

CMD ["uvicorn", "openconnect_transcription.app:app", "--host", "0.0.0.0", "--port", "8080"]
