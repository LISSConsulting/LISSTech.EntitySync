# LISSTech EntitySync MCP Server

The MCP server exposes EntitySync connection, discovery, planning, and guarded apply tools to MCP clients. It supports two transports from one executable:

| Transport | Configuration | Intended use |
|---|---|---|
| stdio | Default, or `MCP_TRANSPORT=stdio` | Local desktop MCP clients |
| Streamable HTTP | `MCP_TRANSPORT=http` | Coolify and other container platforms |

HTTP mode serves:

| Route | Authentication | Purpose |
|---|---|---|
| `/mcp` | OAuth 2.1 bearer access token | Streamable HTTP MCP endpoint |
| `/.well-known/oauth-protected-resource/mcp` | None | RFC 9728 protected-resource metadata |
| `/health` | None | Container and Coolify health probe |

HTTP mode is an OAuth resource server; it does not issue tokens or handle interactive login. It validates signed JWT access tokens through the configured authorization server, including signature, issuer, expiration, audience, and scope. The OAuth access token is never forwarded to vendor APIs.

## Coolify Deployment

1. Configure an OAuth 2.1 authorization server that issues signed JWT access tokens and exposes OAuth authorization-server metadata plus OIDC/JWKS discovery. Register `https://<domain>/mcp` as the resource/audience and allow the `mcp:tools` scope. Enable dynamic client registration when the MCP clients require it, or register those clients manually.
2. Create a Docker Compose resource from this Git repository and use the root `docker-compose.yaml` Compose file.
3. Set `MCP_OAUTH_AUTHORITY` to the authorization server issuer URL.
4. Set `MCP_OAUTH_RESOURCE` to the canonical public MCP URL, such as `https://<domain>/mcp`. This exact value is advertised to MCP clients as the OAuth resource.
5. Set `MCP_OAUTH_AUDIENCE` to the value expected in the access token's `aud` claim. It can differ from the public resource URI for providers such as Microsoft Entra ID.
6. Set `MCP_OAUTH_SCOPES` to the space-delimited scopes clients should request. Set `MCP_OAUTH_REQUIRED_SCOPE` to the single scope value expected in the validated token's `scope` or `scp` claim. They can differ because Entra advertises a full permission URI but emits its short value in `scp`.
   For OAuth clients that cannot resolve the authorization server's metadata layout, set `MCP_OAUTH_AUTHORIZATION_ENDPOINT`, `MCP_OAUTH_TOKEN_ENDPOINT`, and `MCP_OAUTH_PUBLIC_CLIENT_ID` together. The server then preserves the standard RFC 9728 challenge and adds explicit, non-secret endpoint and public-client hints. The client must use PKCE, and its loopback callback URI must be registered with the authorization server.
7. Add the environment variables for the vendors the server will use.
8. Assign the domain to the `entitysync-mcp` service on container port `8080`.
9. Deploy and confirm that `https://<domain>/health` returns `{"status":"healthy"}` and `https://<domain>/.well-known/oauth-protected-resource/mcp` advertises the expected resource and authorization server.
10. Configure the MCP client with URL `https://<domain>/mcp`. A compatible client discovers the authorization server from the protected-resource metadata and performs the OAuth authorization flow.

Do not put credentials in `docker-compose.yaml` or commit a populated `.env` file. Coolify injects the values referenced by the Compose service.

### Vendor Variables

Configure only the vendors the deployment needs. The MCP `connect_vendor` tool reads these values when it creates an adapter.

| Vendor | Required variables |
|---|---|
| HaloPSA | `HALO_BASE_URL`, `HALO_CLIENT_ID`, `HALO_CLIENT_SECRET` |
| NetSuite | `NETSUITE_ACCOUNT_ID`, `NETSUITE_CONSUMER_KEY`, `NETSUITE_CONSUMER_SECRET`, `NETSUITE_TOKEN_ID`, `NETSUITE_TOKEN_SECRET` |
| N-central | `NCENTRAL_BASE_URL`, `NCENTRAL_USER_API_TOKEN`, `NCENTRAL_SERVICE_ORG_ID` |
| AgentController | `AGENTCONTROLLER_AUTH_BASE_URL`, `AGENTCONTROLLER_ENTRA_TENANT_ID`, `AGENTCONTROLLER_ENTRA_CLIENT_ID`, `AGENTCONTROLLER_ENTRA_CLIENT_SECRET`, `AGENTCONTROLLER_ENTRA_SCOPE` |
| Bill.com | `BILLCOM_API_TOKEN` |

The root `.env.example` lists the minimal local Compose variables. `docker-compose.yaml` also passes through optional adapter settings documented in the main README.

DPAPI-backed EntitySync profiles are Windows-only and are intentionally not mounted into the Linux container. Use Coolify secret environment variables for container deployments.

Remote `connect_vendor` calls cannot supply endpoints or credentials. Those values are server-managed, and vendor base URLs must use HTTPS. A connection receives a stable ID and generation; use distinct connection IDs when a future configuration provider exposes multiple accounts for the same vendor.

For AgentController, the MCP host uses the configured Entra service principal with the OAuth 2.0 `client_credentials` grant, then exchanges that Entra access token at `POST /v1/operator-token/exchange`. The exchange response supplies the operations/PostgREST base URL and short-lived bearer token; callers cannot provide either value. Configure `AGENTCONTROLLER_ENTRA_SCOPE` as the AgentController application audience plus `/.default`. The service principal must be assigned the `EntitySync.CustomerScopeSync` Entra app role and registered in AgentController with only `customer_scope_sync:write`. Tokens remain in memory and a rejected operations token is exchanged once before one retry.

## Safe Workflow

1. Call `connect_vendor` for the source and target and retain both connection IDs.
2. Call `create_sync_plan` with those connection IDs. Planning performs no writes.
3. Call `get_sync_plan` until every page has been inspected.
4. Call `approve_sync_plan` with the final inspected digest.
5. Call `apply_sync_plan` with `apply=false` for a dry run.
6. Call `apply_sync_plan` with `apply=true` only after review. Approval is consumed, so the plan cannot be replayed.

Plans expire after four hours and are bound to the exact source and target connection generations used during planning. Reconnecting either account invalidates existing plans. Each tenant is limited to 20 retained plans, 32 connections, and 5,000 source or target entities per plan side.

HaloPSA-to-NCentral and HaloPSA-to-Bill.com apply workflows require source integration-link writebacks that currently exist only in the reviewed PowerShell executor. The MCP application executor rejects those workflows instead of performing incomplete target-only writes.

## Operational Model

- Run exactly one replica. Connections and plans are partitioned by the validated OAuth `sub` claim, so each authorization-server subject has isolated in-memory state.
- A restart clears connections and plans. Reconnect vendors and create a fresh plan after each deployment or restart.
- Creating a plan is read-only. Applying writes requires digest approval and `apply=true`; the default is a dry run.
- `/health` proves that the process is serving HTTP. It does not call vendor APIs; use the MCP `test_connection` tool for vendor connectivity.
- Access-token lifetime, revocation, user consent, client registration, and signing-key rotation belong to the authorization server. The MCP server refreshes signing keys from its discovery metadata.

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
