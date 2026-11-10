# The whole Test Lab (API + web app) in one container, for hosting on a single service.
# The web app is served on $PORT (default 3000); the API stays inside the container on 5110.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY . .
RUN dotnet publish src/Procurement.TestLab -c Release -o /out/api

FROM node:24-bookworm-slim AS web
WORKDIR /app
ENV NEXT_TELEMETRY_DISABLED=1 NEXT_OUTPUT=standalone
COPY web/package.json web/package-lock.json ./
COPY web/scripts ./scripts
RUN npm ci --no-audit --no-fund
COPY web ./
RUN node scripts/copy-monaco.mjs && npm run build

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=web /usr/local/bin/node /usr/local/bin/node
COPY --from=api /out/api ./api
COPY --from=web /app/.next/standalone ./web
COPY --from=web /app/.next/static ./web/.next/static
COPY --from=web /app/public ./web/public
COPY scripts/container-start.sh ./start.sh
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 HOSTNAME=0.0.0.0 PORT=3000 \
    TESTLAB_URL=http://127.0.0.1:5110 TESTLAB_RESULTS_DIR=/tmp/testlab
EXPOSE 3000
USER app
CMD ["sh", "./start.sh"]
