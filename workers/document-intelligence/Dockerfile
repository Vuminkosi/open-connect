FROM python:3.12-slim AS base
COPY --from=ghcr.io/astral-sh/uv:0.13.6 /uv /uvx /bin/
ENV UV_LINK_MODE=copy PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app/workers/document-intelligence

FROM base AS dependencies
COPY workers/common/pyproject.toml /app/workers/common/pyproject.toml
COPY workers/common/src/ /app/workers/common/src/
COPY workers/common/verify_install.py /app/workers/common/verify_install.py
COPY workers/document-intelligence/pyproject.toml workers/document-intelligence/uv.lock ./
RUN uv sync --locked --no-cache --no-dev --no-install-project && .venv/bin/python ../common/verify_install.py

FROM base AS runtime
# Extract only the executable: the Debian tesseract package's language-model
# dependencies must not be installed into the serving image.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libtesseract5 \
    && cd /tmp && apt-get download tesseract-ocr \
    && dpkg-deb -x tesseract-ocr_*.deb /tmp/tesseract-package \
    && install -m 0755 /tmp/tesseract-package/usr/bin/tesseract /usr/local/bin/tesseract \
    && ldd /usr/local/bin/tesseract | (! grep 'not found') \
    && rm -rf /tmp/tesseract-package /tmp/tesseract-ocr_*.deb /var/lib/apt/lists/*
ENV PATH="/app/workers/document-intelligence/.venv/bin:$PATH" \
    PYTHONPATH=/app/workers/document-intelligence/src
COPY --from=dependencies /app/workers/document-intelligence/.venv ./.venv
COPY workers/document-intelligence/src/ ./src/
USER 10001:10001
CMD ["uvicorn", "openconnect_document_intelligence.app:app", "--uds", "/run/openconnect/worker.sock", "--no-access-log", "--limit-concurrency", "16"]
