FROM python:3.12-slim AS base
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /uvx /bin/
ENV UV_LINK_MODE=copy PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app/workers/vision

FROM base AS dependencies
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/common/verify_install.py /app/workers/common/verify_install.py
COPY workers/vision/pyproject.toml workers/vision/uv.lock ./
RUN uv sync --locked --no-cache --no-dev --no-install-project && .venv/bin/python ../common/verify_install.py

FROM base AS runtime
ENV PATH="/app/workers/vision/.venv/bin:$PATH" PYTHONPATH=/app/workers/vision/src \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
COPY --from=dependencies /app/workers/vision/.venv ./.venv
COPY workers/vision/src/ ./src/
USER 10001:10001
CMD ["uvicorn", "openconnect_vision.app:app", "--uds", "/run/openconnect/vision.sock", "--no-access-log", "--limit-concurrency", "16"]
