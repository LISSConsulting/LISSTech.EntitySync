# LISSTech EntitySync MCP Server

The MCP server exposes EntitySync connection, discovery, planning, and guarded apply tools to MCP clients. Agents should treat **EntitySync**, **Entity Sync**, and contextual **ES** as aliases, and route natural-language requests such as **sync clients**, **client sync**, **customer sync**, **account sync**, **company sync**, cross-vendor reconciliation, and vendor-record questions to this server. It supports two transports from one executable:

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
7. Set `POSTGRES_PASSWORD` and set `DATABASE_URL` to the matching PostgreSQL connection string. The Compose stack provisions one PostgreSQL 18 service and persistent volume for permanent exclusions, scheduler change-state checkpoints, and migration coordination.
8. Set the HaloPSA, NetSuite, N-central, BILL.com, and Sophos Central variables listed below. They are required by `entitysync-scheduler`. Set `SCHEDULER_RUN_TOKEN` to a high-entropy secret of 32–256 non-whitespace characters for authenticated manual runs.
9. Assign the public domain only to `entitysync-mcp` on container port `8080`. Do not assign a domain or host port to `entitysync-scheduler`; all scheduler routes are intentionally Compose-network-only.
   The reverse proxy must overwrite, not append, forwarded headers. The MCP application does not trust arbitrary `X-Forwarded-*` headers and uses the configured canonical OAuth resource rather than request host data.
10. Deploy and confirm that `https://<domain>/health` returns `{"status":"healthy"}` and `https://<domain>/.well-known/oauth-protected-resource/mcp` advertises the expected resource and authorization server. Coolify should also report `entitysync-scheduler` healthy from its internal `/health` probe.
11. Configure the MCP client with URL `https://<domain>/mcp`. A compatible client discovers the authorization server from the protected-resource metadata and performs the OAuth authorization flow.

Do not put credentials in `docker-compose.yaml` or commit a populated `.env` file. Coolify injects the values referenced by the Compose service.

### Scheduled Full-Chain Reconciliation

`entitysync-scheduler` runs this fixed, ordered, changed-only chain:

```text
NetSuite Customer -> HaloPSA Client -> N-central Customer
                                    -> BILL.com Client
                                    -> Sophos Central Customer
```

- By default, it runs once immediately after database migrations and then every 12 hours measured from completion of the previous run. Set `SCHEDULER_AUTOMATIC_RUNS_ENABLED=false` to suppress both automatic paths while retaining authenticated `POST /run`; `/status.nextRunAt` remains `null`. A failed run remains visible in status, but liveness stays healthy and there is no immediate retry.
- Every edge includes active and inactive sources. It updates only persistently linked targets; `createMissing` is always false, so unmatched records and fuzzy matches never write unattended.
- The edges execute in order. NetSuite-to-HaloPSA completes before HaloPSA is reread for each leaf, and a failed edge stops all later edges in that run.
- The first successful run is an intentional baseline reconciliation for all four edges. PostgreSQL stores each edge's target identity, canonical SHA-256 desired-payload hash, digest schema version, and applied timestamp only after a successful write. Later runs skip identical mapped writes.
- Recurring BILL.com planning suppresses orphan target-only deletion. A linked BILL.com rename still follows BILL.com's required replacement flow: create the renamed value, write its ID to HaloPSA, and only then delete the old value.
- Route scopes derive from non-secret vendor account identities. One PostgreSQL advisory lock covers the complete ordered chain and prevents overlapping replicas.
- Changed-only detection compares each newly mapped desired payload with its successful PostgreSQL checkpoint. It does **not** detect target-side drift when source data and mapping behavior remain unchanged.
- Every route plan is fully paged, digest-checked, approved, and applied by the sidecar. A connection, planning, validation, approval, or apply failure is bounded to the run and recorded without vendor payloads or credentials.

The scheduler exposes only these internal routes:

| Route | Meaning |
|---|---|
| `/health` | Process liveness only; always `{"status":"healthy"}` while HTTP is serving, including after a failed reconciliation |
| `/status` | Bounded aggregate snapshot with exactly `state`, `lastStartedAt`, `lastCompletedAt`, `nextRunAt`, `planId`, `total`, `changed`, `unchanged`, `policySkipped`, `succeeded`, `failed`, `applySkipped`, and `error` |
| `POST /run` | Queue one immediate full-chain reconciliation; requires `Authorization: Bearer <SCHEDULER_RUN_TOKEN>`, returns `202` when queued, and returns `409` while a run is queued or active |

`/health` and `/status` are unauthenticated for private-network probes and inspection. `POST /run` validates the bearer token with a constant-time digest comparison. Keep every scheduler route on the private Compose network and invoke it only from another service or the Coolify container console:

```sh
curl --fail-with-body --request POST \
  --header "Authorization: Bearer ${SCHEDULER_RUN_TOKEN}" \
  http://entitysync-scheduler:8080/run
```

An accepted manual run executes asynchronously, never overlaps another local run, and leaves the existing scheduled deadline unchanged unless it runs through that deadline. Poll `/status` for the result.

### Vendor Variables

NetSuite, HaloPSA, N-central, BILL.com, and Sophos Central are mandatory for the scheduled chain. The MCP service uses the same variables when `connect_vendor` creates an adapter.

| Vendor | Required variables |
|---|---|
| HaloPSA | `HALO_BASE_URL`, `HALO_CLIENT_ID`, `HALO_CLIENT_SECRET`, `HALO_NCENTRAL_INTEGRATION_ID` |
| NetSuite | `NETSUITE_ACCOUNT_ID`, `NETSUITE_CONSUMER_KEY`, `NETSUITE_CONSUMER_SECRET`, `NETSUITE_TOKEN_ID`, `NETSUITE_TOKEN_SECRET` |
| N-central | `NCENTRAL_BASE_URL`, `NCENTRAL_USER_API_TOKEN`, `NCENTRAL_SERVICE_ORG_ID`, `NCENTRAL_SOAP_USERNAME`, `NCENTRAL_SOAP_PASSWORD` |
| AgentController | `AGENTCONTROLLER_AUTH_BASE_URL`, `AGENTCONTROLLER_ENTRA_TENANT_ID`, `AGENTCONTROLLER_ENTRA_CLIENT_ID`, `AGENTCONTROLLER_ENTRA_CLIENT_SECRET`, `AGENTCONTROLLER_ENTRA_SCOPE` |
| BILL.com | `BILLCOM_API_TOKEN`; optional `BILLCOM_BASE_URL` and `BILLCOM_CLIENT_FIELD_NAME` override production defaults |
| Sophos Central | `SOPHOS_CENTRAL_CLIENT_ID`, `SOPHOS_CENTRAL_CLIENT_SECRET`; tenant creation defaults remain optional because the changed-only scheduler does not create unmatched tenants |

The root `.env.example` lists the minimal local Compose variables. `docker-compose.yaml` also passes through optional adapter settings documented in the main README.

DPAPI-backed EntitySync profiles are Windows-only and are intentionally not mounted into the Linux container. Use Coolify secret environment variables for container deployments.

Remote `connect_vendor` calls cannot supply endpoints or credentials. Those values are server-managed, and vendor base URLs must use HTTPS. A connection receives a stable ID and generation; use distinct connection IDs when a future configuration provider exposes multiple accounts for the same vendor.

Permanent exclusions are stored in PostgreSQL and scoped to the exact tenant, source/target vendors, connection IDs, and entity types. Manage them with `list_entity_exclusions`, `add_entity_exclusion`, and `remove_entity_exclusion`; use immutable vendor source IDs, never names. An empty exclusion list is valid. If exclusion storage cannot be read, `create_sync_plan` with `createMissing=true` fails before returning a plan, and apply revalidates exclusions before any create. AgentController authoritative customer-scope routes reject exclusions because omitting a row could retire an existing scope.

For AgentController, the MCP host uses the configured Entra service principal with the OAuth 2.0 `client_credentials` grant, then exchanges that Entra access token at `POST /v1/operator-token/exchange`. The exchange response supplies the operations/PostgREST base URL and short-lived bearer token; callers cannot provide either value. Configure `AGENTCONTROLLER_ENTRA_SCOPE` as the AgentController application audience plus `/.default`. The service principal must be assigned the `EntitySync.CustomerScopeSync` Entra app role and registered in AgentController with only `customer_scope_sync:write`. Tokens remain in memory and a rejected operations token is exchanged once before one retry.

## Entity Inspection

Use `get_entities` for read-only factual questions about connected vendor records. Supply `search` and a small `count` to narrow the result; set `includeDetails` to `true` when the question needs vendor detail reads such as addresses.

```json
{
  "vendor": "NetSuite",
  "entityType": "Customer",
  "connectionId": "netsuite",
  "search": "Ursula Capital",
  "count": 5,
  "includeDetails": true
}
```

Each result includes the canonical primary, billing, and shipping addresses when available, plus contact data, site context, lifecycle timestamps, external IDs, and non-secret custom fields. Multiple matches remain separate records so the caller can identify ambiguity instead of guessing.

## Safe Workflow

1. Call `connect_vendor` for the source and target and retain both connection IDs.
2. Call `create_sync_plan` with those connection IDs. Planning performs no writes. For a focused plan, pass `sourceSearch` and `sourceCount` to bound the vendor query and `sourceEntityId` to require the exact immutable vendor ID. A missing or duplicate exact ID fails before target discovery.
3. Call `get_sync_plan` until every page has been inspected.
4. Call `approve_sync_plan` with the final inspected digest.
5. Call `apply_sync_plan` with `apply=false` for a dry run.
6. Call `apply_sync_plan` with `apply=true` once, only after review. This starts background execution and immediately returns its current snapshot; it does not wait for writes to finish. Approval is consumed, so the plan cannot be replayed.
7. Poll `get_sync_plan_apply` with the plan ID until the snapshot status is `Applied` or `Failed`. Repeating `apply_sync_plan` for that plan returns the existing operation; it never retries the operation or duplicates writes.

Plans expire after four hours and are bound to the exact source and target connection generations used during planning. Reconnecting either account invalidates existing plans. Each tenant is limited to 20 retained plans, 32 connections, and 5,000 source or target entities per plan side.

Focused one-customer example:

```json
{
  "sourceVendor": "NetSuite",
  "targetVendor": "HaloPSA",
  "sourceConnectionId": "netsuite",
  "targetConnectionId": "halopsa",
  "sourceEntityType": "Customer",
  "targetEntityType": "Client",
  "sourceSearch": "Degmor",
  "sourceCount": 10,
  "sourceEntityId": "1816",
  "createMissing": true
}
```

Plan pages expose `sourceId` and `targetId` alongside display names so approval can be based on immutable identities. `sourceEntityId` is an assertion over the bounded vendor query, not a name match; combine it with a selective `sourceSearch` when the vendor account is large.

The MCP application executor performs the same target and source-writeback workload as the reviewed PowerShell executor. HaloPSA-to-N-central writes update HaloPSA integration links; HaloPSA-to-Bill.com writes record the BILL custom-field value ID on the HaloPSA client; AgentController plans use one authoritative batch.

Sophos Central partner and organization tenants are readable `Customer` entities. Partner credentials can create tenants and patch `showAs`; organization credentials remain read-only because the Organization API has no tenant write route.

## Operational Model

- Run exactly one replica. Connections and plans are partitioned by the validated issuer plus OAuth `sub` claim, so equal subjects from different issuers cannot share in-memory state. Permanent exclusion audit actors use the same immutable identity.
- A restart clears connections, plans, and in-memory apply-operation snapshots. In-flight applies are not recovered or resumed, and cannot be polled after restart. Reconnect vendors and create a fresh plan after each deployment or restart.
- Creating a plan and polling apply status are read-only. Applying writes requires digest approval and `apply=true`; the default is a synchronous dry run.
- `/health` proves that the process is serving HTTP. It does not call vendor APIs; use the MCP `test_connection` tool for vendor connectivity.
- Credential-bearing vendor clients reject redirects, cap each response at 8 MiB, and share per-origin request spacing and concurrency limits. N-central SOAP endpoints must be relative paths on the configured HTTPS origin. Vendor pagination and Halo site scans fail closed at bounded scan limits.
- Both application images are framework-dependent and use the same digest-pinned SDK and ASP.NET runtime images; local release builds remain self-contained single files.
- Access-token lifetime, revocation, user consent, client registration, and signing-key rotation belong to the authorization server. The MCP server refreshes signing keys from its discovery metadata.

## Local Development

Build or run either local application:

```powershell
just mcp-build
just mcp-run
just scheduler-build
just scheduler-run
```

Run the same Compose deployment locally after populating an ignored `.env` file:

```powershell
docker compose up --build
```

Both application containers run as the image's non-root `$APP_UID`, have read-only root filesystems, drop all Linux capabilities, set `no-new-privileges`, use an init process and `unless-stopped` restart policy, and receive only a 64 MiB writable in-memory `/tmp`. The scheduler adds no state or credential volume; PostgreSQL is its only durable state.
