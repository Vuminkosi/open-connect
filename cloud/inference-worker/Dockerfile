FROM python:3.12-slim AS dependencies
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /uvx /bin/
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy CMAKE_BUILD_PARALLEL_LEVEL=2 CMAKE_ARGS="-DGGML_NATIVE=OFF -DGGML_OPENMP=ON"
RUN apt-get update && apt-get install -y --no-install-recommends build-essential cmake \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app/workers/inference
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/inference/pyproject.toml workers/inference/uv.lock ./
RUN uv sync --locked --no-dev --extra cloud-inference --no-install-project

FROM python:3.12-slim AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --no-create-home inference
COPY --from=dependencies /app/workers/inference/.venv /app/workers/inference/.venv
COPY --chown=inference:inference workers/inference/src/ /app/workers/inference/src/
WORKDIR /app/workers/inference
ENV PATH="/app/workers/inference/.venv/bin:$PATH" PYTHONPATH=/app/workers/inference/src \
    PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 \
    OPENCONNECT_CLOUD_INFERENCE_ENABLED=true OC_WORKER_ENGINE=real OPENCONNECT_FAKE_ENGINE=0 \
    OC_INFERENCE_API_BASE_URL="" INFERENCE_CLOUD_URL="" OC_INFERENCE_MODEL=local-only
USER inference
EXPOSE 8080
CMD ["uvicorn", "openconnect_inference.app:app", "--host", "0.0.0.0", "--port", "8080", "--ssl-certfile", "/run/openconnect-cloud/server.crt", "--ssl-keyfile", "/run/openconnect-cloud/server.key", "--ssl-ca-certs", "/run/openconnect-cloud/gateway-client-ca.crt", "--ssl-cert-reqs", "2", "--no-access-log"]
