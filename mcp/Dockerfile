FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

WORKDIR /src

COPY src/LISSTech.EntitySync.csproj src/
COPY mcp/LISSTech.EntitySync.Mcp.csproj mcp/
RUN dotnet restore mcp/LISSTech.EntitySync.Mcp.csproj

COPY src/ src/
COPY mcp/ mcp/
RUN dotnet publish mcp/LISSTech.EntitySync.Mcp.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --property:PublishSingleFile=false \
    --property:SelfContained=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    MCP_TRANSPORT=http

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "lisstech-entitysync-mcp.dll"]
