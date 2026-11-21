# Pin this major-minor image to its registry digest in release builds.
FROM python:3.12-slim AS base

COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /uvx /bin/
ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    # Offline by default: this worker must never download models at runtime.
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HOME=/models

WORKDIR /app/workers/inference

FROM base AS dependencies
ENV CMAKE_BUILD_PARALLEL_LEVEL=2 CMAKE_ARGS="-DGGML_NATIVE=OFF -DGGML_OPENMP=ON"
RUN apt-get update && apt-get install -y --no-install-recommends build-essential cmake \
    && rm -rf /var/lib/apt/lists/*
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/common/verify_install.py /app/workers/common/verify_install.py
COPY workers/inference/pyproject.toml workers/inference/uv.lock ./
RUN uv sync --locked --no-cache --no-dev --extra extraction --extra cloud-inference --no-install-project && .venv/bin/python ../common/verify_install.py

FROM base AS runtime
ENV PATH="/app/workers/inference/.venv/bin:$PATH" \
    PYTHONPATH=/app/workers/inference/src \
    OC_MODEL_CACHE_DIR=/models \
    MODEL_DIR=/models \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=dependencies /app/workers/inference/.venv /app/workers/inference/.venv
COPY workers/inference/src/ /app/workers/inference/src/
RUN mkdir -p /models && chown -R 10001:10001 /app /models
USER 10001:10001

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health/ready', timeout=2)"]

CMD ["uvicorn", "openconnect_inference.app:app", "--host", "0.0.0.0", "--port", "8080"]
