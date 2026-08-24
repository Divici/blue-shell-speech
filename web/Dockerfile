# syntax=docker/dockerfile:1

# ---- deps ------------------------------------------------------------------
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

# ---- build -----------------------------------------------------------------
FROM node:22-alpine AS build
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1

# NEXT_PUBLIC_* values are inlined into the bundle at build time, so they must be
# present here rather than at container start.
#
# NEXT_PUBLIC_SITE_ENV distinguishes the dev-subscription deployment from the real site.
# NODE_ENV cannot: it is "production" in both. When this is "production" the build FAILS
# if contact details are missing or still placeholders — see lib/practice-contact.ts.
#
# Defaults are the development placeholders. Real values are supplied by the production
# deploy pipeline and never appear in this repository.
ARG NEXT_PUBLIC_SITE_ENV=development
ARG NEXT_PUBLIC_PRACTICE_PHONE=410-555-0100
ARG NEXT_PUBLIC_PRACTICE_EMAIL=hello@blueshellspeech.example
ENV NEXT_PUBLIC_SITE_ENV=$NEXT_PUBLIC_SITE_ENV
ENV NEXT_PUBLIC_PRACTICE_PHONE=$NEXT_PUBLIC_PRACTICE_PHONE
ENV NEXT_PUBLIC_PRACTICE_EMAIL=$NEXT_PUBLIC_PRACTICE_EMAIL

RUN npm run build

# ---- runtime ---------------------------------------------------------------
FROM node:22-alpine AS runtime
WORKDIR /app

ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000
ENV HOSTNAME=0.0.0.0

# Non-root. A container running as root turns a code-execution bug into a container
# takeover; this one renders PHI.
RUN addgroup -g 1001 -S nodejs && adduser -S nextjs -u 1001

COPY --from=build --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=build --chown=nextjs:nodejs /app/.next/static ./.next/static
COPY --from=build --chown=nextjs:nodejs /app/public ./public

USER nextjs
EXPOSE 3000

# Container Apps probes this. It must not depend on the API being awake — `web` serving
# the marketing site is useful even while `api` is cold.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:3000/api/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "server.js"]
