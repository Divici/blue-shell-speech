# syntax=docker/dockerfile:1

# ---- build -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first, against project files only, so a source-only change does not invalidate
# the restore layer.
COPY Directory.Build.props ./
COPY src/Practice.Domain/Practice.Domain.csproj                 src/Practice.Domain/
COPY src/Practice.Application/Practice.Application.csproj       src/Practice.Application/
COPY src/Practice.Infrastructure/Practice.Infrastructure.csproj src/Practice.Infrastructure/
COPY src/Practice.Api/Practice.Api.csproj                       src/Practice.Api/
RUN dotnet restore src/Practice.Api/Practice.Api.csproj

COPY src/ src/
RUN dotnet publish src/Practice.Api/Practice.Api.csproj \
      -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Culture data is required: timestamps render in America/New_York (CLAUDE.md conventions),
# which invariant globalization cannot do.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
RUN apk add --no-cache icu-libs tzdata

# Non-root. This process holds the database connection and every PHI code path.
RUN addgroup -g 1001 -S dotnetgroup && adduser -S dotnetuser -u 1001 -G dotnetgroup
COPY --from=build --chown=dotnetuser:dotnetgroup /app/publish .
USER dotnetuser

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Practice.Api.dll"]
