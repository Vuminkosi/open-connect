FROM python:3.12-slim AS base
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /uvx /bin/
ENV UV_LINK_MODE=copy PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app/workers/knowledge

FROM base AS dependencies
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/common/verify_install.py /app/workers/common/verify_install.py
COPY workers/knowledge/pyproject.toml workers/knowledge/uv.lock ./
RUN uv sync --locked --no-cache --no-dev --no-install-project && .venv/bin/python ../common/verify_install.py

FROM base AS runtime
ENV PATH="/app/workers/knowledge/.venv/bin:$PATH" \
    PYTHONPATH=/app/workers/knowledge/src \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
COPY --from=dependencies /app/workers/knowledge/.venv ./.venv
COPY workers/knowledge/src/ ./src/
COPY workers/knowledge/migrations/ ./migrations/
RUN mkdir -p /models && chown 10001:10001 /models
USER 10001:10001
EXPOSE 8080
CMD ["uvicorn", "openconnect_knowledge.app:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log", "--limit-concurrency", "16"]
