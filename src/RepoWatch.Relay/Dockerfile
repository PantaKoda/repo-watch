# Repo Watch relay: build from the repository root:
#   docker build -f src/RepoWatch.Relay/Dockerfile -t repowatch-relay .
# Run (the webhook secret comes from your secret store, never from the image):
#   docker run -p 8080:8080 -e Relay__WebhookSecret=... -v repowatch-relay-data:/app/data repowatch-relay
# Put it behind HTTPS (a reverse proxy or your platform's TLS termination): GitHub and the desktop app
# only talk to it over https://.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/RepoWatch.Core/ src/RepoWatch.Core/
COPY src/RepoWatch.Relay/ src/RepoWatch.Relay/
RUN dotnet restore src/RepoWatch.Relay/RepoWatch.Relay.csproj --locked-mode
RUN dotnet publish src/RepoWatch.Relay/RepoWatch.Relay.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /app/data && chown app /app/data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Relay__DatabasePath=/app/data/relay.db
EXPOSE 8080
# Health: GET /healthz (configure it in your platform; the runtime image has no HTTP client for HEALTHCHECK).
ENTRYPOINT ["dotnet", "RepoWatch.Relay.dll"]
