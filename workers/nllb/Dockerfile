# Pin this major-minor image to its registry digest in release builds.
FROM python:3.12-slim AS base

COPY --from=ghcr.io/astral-sh/uv:0.7.12 /uv /uvx /bin/
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    OC_MODEL_CACHE_DIR=/models \
    MODEL_DIR=/models

WORKDIR /app

FROM base AS dependencies
COPY pyproject.toml uv.lock ./
RUN uv sync --locked --no-dev --no-install-project

FROM base AS runtime
ENV PATH="/app/.venv/bin:$PATH" \
    PYTHONPATH=/app/src \
    OC_MODEL_CACHE_DIR=/models \
    MODEL_DIR=/models \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1
COPY --from=dependencies /app/.venv /app/.venv
COPY src/ /app/src/
RUN mkdir -p /models && chown -R 10001:10001 /app /models
USER 10001:10001

EXPOSE 8080
# Liveness only: readiness is truthful (unready when the model, tokenizer, or
# license is absent) and must not mark the container unhealthy for a missing
# mounted model.
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health/live', timeout=2)"]

CMD ["uvicorn", "openconnect_nllb.app:app", "--host", "0.0.0.0", "--port", "8080"]
