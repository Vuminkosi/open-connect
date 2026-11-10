# navigator-playwright worker: pinned Node LTS + pinned Playwright/Chromium.
# No model volume. Per-run browser profiles are created under /tmp and removed
# after each execution (see src/server.js withFreshProfile).
FROM node:22.17.1-bookworm-slim AS base

ENV NODE_ENV=production \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PLAYWRIGHT_SKIP_BROWSER_GC=1

WORKDIR /app

FROM base AS dependencies
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund

FROM base AS browsers
COPY --from=dependencies /app/node_modules /app/node_modules
COPY package.json package-lock.json ./
# Pinned Chromium for the pinned playwright-core (1.49.1). System deps included.
RUN npx --no-install playwright-core@1.49.1 install --with-deps chromium \
    && rm -rf /root/.npm /tmp/*

FROM base AS runtime
RUN groupadd -r navuser --gid 10001 \
    && useradd -r -g navuser --uid 10001 --create-home --home-dir /home/navuser navuser \
    && mkdir -p /app /tmp/nav-tmp /ms-playwright \
    && chown -R 10001:10001 /app /home/navuser /tmp/nav-tmp
COPY --from=dependencies /app/node_modules /app/node_modules
COPY --from=browsers /ms-playwright /ms-playwright
COPY package.json ./
COPY src/ ./src/
RUN chown -R 10001:10001 /app /ms-playwright

USER 10001:10001

EXPOSE 8080
ENV PORT=8080 \
    TMPDIR=/tmp/nav-tmp \
    PLAYWRIGHT_LAUNCH=1

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD ["node", "-e", "fetch('http://127.0.0.1:8080/health/ready').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"]

CMD ["node", "src/server.js"]
