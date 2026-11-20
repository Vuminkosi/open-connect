FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=8091
COPY --chown=node:node gateway.mjs local-server.mjs start.mjs admission.mjs control-plane.mjs configuration.mjs ./
USER node
EXPOSE 8091
CMD ["node", "start.mjs"]
