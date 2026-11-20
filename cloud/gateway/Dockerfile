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

FROM node:24-bookworm-slim AS node

FROM python:3.12-slim AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --no-create-home gateway
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=dependencies /app/workers/inference/.venv /app/workers/inference/.venv
COPY --chown=gateway:gateway workers/inference/src/openconnect_inference/__init__.py workers/inference/src/openconnect_inference/cloud_consumption.py workers/inference/src/openconnect_inference/cloud_execution.py workers/inference/src/openconnect_inference/cloud_llama_runtime.py workers/inference/src/openconnect_inference/cloud_quote.py /app/workers/inference/src/openconnect_inference/
WORKDIR /app/gateway
COPY --chown=gateway:gateway cloud/gateway/*.mjs ./
ENV NODE_ENV=production PORT=8091 PYTHONDONTWRITEBYTECODE=1 HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
USER gateway
EXPOSE 8091
CMD ["node", "start.mjs"]
