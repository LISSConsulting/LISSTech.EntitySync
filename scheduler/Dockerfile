
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine@sha256:8a80a27ddac789b4cb6d09d244f9c8d840da599c5ad22f7233c04be470e55261 AS build

WORKDIR /src

COPY src/Core/LISSTech.EntitySync.Core.csproj src/Core/
COPY src/Ports/LISSTech.EntitySync.Ports.csproj src/Ports/
COPY src/Matching/LISSTech.EntitySync.Matching.csproj src/Matching/
COPY src/Mapping/LISSTech.EntitySync.Mapping.csproj src/Mapping/
COPY src/Planning/LISSTech.EntitySync.Planning.csproj src/Planning/
COPY src/Application/LISSTech.EntitySync.Application.csproj src/Application/
COPY src/Adapters/LISSTech.EntitySync.Adapters.csproj src/Adapters/
COPY src/Runtime/LISSTech.EntitySync.Runtime.csproj src/Runtime/
COPY src/Hosting/LISSTech.EntitySync.Hosting.csproj src/Hosting/
COPY db/migrations/ db/migrations/
COPY scheduler/LISSTech.EntitySync.Scheduler.csproj scheduler/
RUN dotnet restore scheduler/LISSTech.EntitySync.Scheduler.csproj

COPY src/ src/
COPY scheduler/ scheduler/
RUN dotnet publish scheduler/LISSTech.EntitySync.Scheduler.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --property:PublishSingleFile=false \
    --property:SelfContained=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine@sha256:b288317d8ed45bb763fa95dbc807cf9d36e3bf9373ec2fac6b6548675f1f4b23 AS runtime

WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/entitysync/keys \
    && chown 1654:1654 /var/lib/entitysync/keys \
    && chmod 0700 /var/lib/entitysync/keys

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

USER 1654:1654

ENTRYPOINT ["dotnet", "lisstech-entitysync-scheduler.dll"]
