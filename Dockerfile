# syntax=docker/dockerfile:1

# ---- Build stage -------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project files only, so this layer is cached until a package reference changes.
# Directory.Build.props and global.json apply to every project, so they are part of the restore inputs.
COPY global.json Directory.Build.props ./
COPY src/SmartBank.Core/SmartBank.Core.csproj src/SmartBank.Core/
COPY src/SmartBank.Infrastructure/SmartBank.Infrastructure.csproj src/SmartBank.Infrastructure/
COPY src/SmartBank.API/SmartBank.API.csproj src/SmartBank.API/
RUN dotnet restore src/SmartBank.API/SmartBank.API.csproj

# Then the sources of the three projects the API needs (the tests, docs and web files are excluded by .dockerignore).
COPY src/ src/
RUN dotnet publish src/SmartBank.API/SmartBank.API.csproj -c Release --no-restore -o /app/publish

# ---- Runtime stage -----------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Run as the unprivileged "app" user that the official .NET images provide (not root), on port 8080
# (ports below 1024 would need root).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

# Render (like most PaaS hosts) terminates TLS in a reverse proxy. Trust X-Forwarded-For/Proto so the app sees the
# real client IP: the per-IP rate limit and the audit log depend on it. Do not expose this container directly.
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

# No HEALTHCHECK instruction: the aspnet image has no curl or wget. On Render set the service's "Health Check Path"
# to /health (liveness, touches nothing). /health/ready also checks the database.

ENTRYPOINT ["dotnet", "SmartBank.API.dll"]
