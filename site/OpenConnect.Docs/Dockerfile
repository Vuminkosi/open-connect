FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY build/supported-frameworks.props build/supported-frameworks.props
COPY src/ src/
COPY site/OpenConnect.Docs/ site/OpenConnect.Docs/
RUN dotnet restore site/OpenConnect.Docs/OpenConnect.Docs.csproj --locked-mode \
    && dotnet publish site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --no-restore -o /out

FROM python:3.12-slim AS gateway-python-dependencies
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /home/app/.aspnet
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=gateway-python-dependencies /usr/local/bin/python3.12 /usr/local/bin/python3 /usr/local/bin/python /usr/local/bin/
COPY --from=gateway-python-dependencies /usr/local/lib/libpython3.12* /usr/local/lib/
COPY --from=gateway-python-dependencies /usr/local/lib/python3.12 /usr/local/lib/python3.12
RUN ldconfig
COPY --from=gateway-python-dependencies /app/workers/inference/.venv /app/workers/inference/.venv
COPY --chown=app:app workers/inference/src/openconnect_inference/__init__.py \
    workers/inference/src/openconnect_inference/cloud_consumption.py \
    workers/inference/src/openconnect_inference/cloud_execution.py \
    workers/inference/src/openconnect_inference/cloud_llama_runtime.py \
    workers/inference/src/openconnect_inference/cloud_quote.py \
    /app/workers/inference/src/openconnect_inference/
WORKDIR /app
COPY --from=build --chown=app:app /out/ ./
COPY --chown=app:app cloud/gateway/*.mjs /app/gateway/
COPY --chown=app:app site/OpenConnect.Docs/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    NODE_ENV=production \
    CLOUD_GATEWAY_ENABLED=true \
    CLOUD_GATEWAY_PORT=8091 \
    PYTHONDONTWRITEBYTECODE=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1
EXPOSE 8080 8091
ENTRYPOINT ["/app/entrypoint.sh"]
