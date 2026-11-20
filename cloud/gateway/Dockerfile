FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=8091
COPY --chown=node:node gateway.mjs local-server.mjs ./
USER node
EXPOSE 8091
CMD ["node", "local-server.mjs"]
