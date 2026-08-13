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
COPY db/migrations/ db/migrations/
COPY mcp/LISSTech.EntitySync.Mcp.csproj mcp/
RUN dotnet restore mcp/LISSTech.EntitySync.Mcp.csproj

COPY src/ src/
COPY mcp/ mcp/
# The container deliberately publishes framework-dependent, multi-file output:
# the pinned aspnet runtime image supplies the shared framework. Local release
# builds remain self-contained single files as configured by the project.
RUN dotnet publish mcp/LISSTech.EntitySync.Mcp.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --property:PublishSingleFile=false \
    --property:SelfContained=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine@sha256:b288317d8ed45bb763fa95dbc807cf9d36e3bf9373ec2fac6b6548675f1f4b23 AS runtime

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    MCP_TRANSPORT=http

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "lisstech-entitysync-mcp.dll"]
