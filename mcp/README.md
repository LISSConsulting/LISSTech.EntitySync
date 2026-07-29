# LISSTech EntitySync MCP Server

The MCP server exposes EntitySync connection, discovery, planning, and guarded apply tools to MCP clients. It supports two transports from one executable:

| Transport | Configuration | Intended use |
|---|---|---|
| stdio | Default, or `MCP_TRANSPORT=stdio` | Local desktop MCP clients |
| Streamable HTTP | `MCP_TRANSPORT=http` | Coolify and other container platforms |

HTTP mode serves:

| Route | Authentication | Purpose |
|---|---|---|
| `/mcp` | `Authorization: Bearer <MCP_API_KEY>` | Streamable HTTP MCP endpoint |
| `/health` | None | Container and Coolify health probe |

## Coolify Deployment

1. Create a Docker Compose resource from this Git repository.
2. Use the root `docker-compose.yaml` Compose file.
3. Let Coolify generate the `SERVICE_PASSWORD_64_MCP_API_KEY` secret referenced by the Compose file. For local Compose, set it to at least 32 characters; generate one with `openssl rand -hex 32`.
4. Add the environment variables for the vendors the server will use.
5. Assign a domain to the `entitysync-mcp` service on container port `8080`.
6. Deploy and confirm that `https://<domain>/health` returns `{"status":"healthy"}`.
7. Retrieve `SERVICE_PASSWORD_64_MCP_API_KEY` from Coolify and configure the MCP client with URL `https://<domain>/mcp` and an `Authorization: Bearer <value>` header.

Do not put credentials in `docker-compose.yaml` or commit a populated `.env` file. Coolify injects the values referenced by the Compose service.

### Vendor Variables

Configure only the vendors the deployment needs. The MCP `connect_vendor` tool reads these values when it creates an adapter.

| Vendor | Required variables |
|---|---|
| HaloPSA | `HALO_BASE_URL`, `HALO_CLIENT_ID`, `HALO_CLIENT_SECRET` |
| NetSuite | `NETSUITE_ACCOUNT_ID`, `NETSUITE_CONSUMER_KEY`, `NETSUITE_CONSUMER_SECRET`, `NETSUITE_TOKEN_ID`, `NETSUITE_TOKEN_SECRET` |
| N-central | `NCENTRAL_BASE_URL`, `NCENTRAL_USER_API_TOKEN`, `NCENTRAL_SERVICE_ORG_ID` |
| Bill.com | `BILLCOM_API_TOKEN` |

The root `.env.example` lists the minimal local Compose variables. `docker-compose.yaml` also passes through optional adapter settings documented in the main README.

DPAPI-backed EntitySync profiles are Windows-only and are intentionally not mounted into the Linux container. Use Coolify secret environment variables for container deployments.

## Operational Model

- Run exactly one replica. Connected adapters and generated plans are held in memory and are shared by clients authorized with the deployment's API key.
- A restart clears connections and plans. Reconnect vendors and create a fresh plan after each deployment or restart.
- Creating a plan is read-only. Applying writes still requires `apply=true`; the default is a dry run.
- `/health` proves that the process is serving HTTP. It does not call vendor APIs; use the MCP `test_connection` tool for vendor connectivity.
- Rotate `SERVICE_PASSWORD_64_MCP_API_KEY` in Coolify and redeploy if the key is disclosed.

## Local Development

Build or run the stdio server:

```powershell
just mcp-build
just mcp-run
```

Run the same Compose deployment locally after populating an ignored `.env` file:

```powershell
docker compose up --build
```

The container is non-root, read-only, has all Linux capabilities dropped, and uses a writable in-memory `/tmp` only.
