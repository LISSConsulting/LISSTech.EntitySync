# LISSTech.EntitySync

**Vendor entity synchronization that refuses to guess silently: connect NetSuite and HaloPSA, build an explainable match plan, review every risky row, then apply only the changes you explicitly approve.**

[![PowerShell 7.4+](https://img.shields.io/badge/PowerShell-7.4+-5391FE?style=for-the-badge&logo=powershell&logoColor=000&labelColor=000)](https://learn.microsoft.com/powershell/)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-8A2BE2?style=for-the-badge&logo=dotnet&logoColor=fff&labelColor=000)](https://dotnet.microsoft.com/)
[![NetSuite](https://img.shields.io/badge/Adapter-NetSuite-FF6B35?style=for-the-badge&labelColor=000)](https://www.netsuite.com/)
[![HaloPSA](https://img.shields.io/badge/Adapter-HaloPSA-4ECDC4?style=for-the-badge&labelColor=000)](https://halopsa.com/)
[![Safe By Default](https://img.shields.io/badge/Safety-Plan_First_Cut_Later-C7F464?style=for-the-badge&labelColor=000)](#-safety-model)

[**Quick Start**](#-quick-start) · [**Architecture**](#%EF%B8%8F-architecture) · [**PowerShell API**](#-powershell-api) · [**Matching**](#-matching-rules) · [**Safety**](#-safety-model) · [**Build**](#-build--test)

---

## 📑 Table Of Contents

- [⚡ Quick Start](#-quick-start)
- [🏗️ Architecture](#%EF%B8%8F-architecture)
- [🎮 PowerShell API](#-powershell-api)
- [🧠 Matching Rules](#-matching-rules)
- [🔐 Configuration](#-configuration)
- [🧨 Safety Model](#-safety-model)
- [🔨 Build & Test](#-build--test)
- [📁 Project Structure](#-project-structure)

---

## ⚡ Quick Start

```powershell
# Build the binary module into .\Module\
just build

# Load the module
Import-Module .\Module\LISSTech.EntitySync.psd1 -Force

# Register vendor adapters. Parameters can also come from environment variables.
Connect-EntitySyncVendor -Vendor NetSuite
Connect-EntitySyncVendor -Vendor HaloPSA

# Plan NetSuite Customer -> HaloPSA Client sync. No writes happen here.
$plan = New-EntitySyncPlan `
  -SourceVendor NetSuite -SourceEntityType Customer `
  -TargetVendor HaloPSA -TargetEntityType Client `
  -CreateMissing

# Export an Excel review workbook. Use the Decision dropdown per row.
$plan | Export-EntitySyncPlan -Path .\netsuite-halo-client-plan.xlsx

# Or export into a directory and let the module generate the file name.
$reviewWorkbook = $plan | Export-EntitySyncPlan -Path $HOME\Downloads -PassThru

# Reload the reviewed workbook back into an executable plan.
$plan = Import-EntitySyncPlan .\netsuite-halo-client-plan.xlsx

# Dry-run the approved plan. Remove -WhatIf only when the plan is clean.
$plan | Invoke-EntitySyncPlan -Apply -WhatIf
```

---

## 🏗️ Architecture

```mermaid
%%{init: {'theme': 'base', 'themeVariables': {'fontFamily': 'monospace', 'fontSize': '13px', 'primaryColor': '#2E71B8', 'primaryBorderColor': '#000', 'primaryTextColor': '#000', 'lineColor': '#000'}}}%%
graph LR
    A["🎮 PowerShell Cmdlets<br/>binary module"] --> B["🔌 ConnectionRegistry<br/>active adapters"]
    B --> C["🟦 NetSuite Adapter<br/>customers"]
    B --> D["🟩 HaloPSA Adapter<br/>clients"]
    C --> E["📦 ExternalEntity<br/>canonical model"]
    D --> E
    E --> F["🧠 WeightedEntityMatcher<br/>explainable scoring"]
    F --> G["📋 EntitySyncPlan<br/>None / Link / Create / Review"]
    G --> H{"🧨 Invoke-EntitySyncPlan<br/>-Apply required"}
    H -->|"WhatIf / Review"| I["🛑 no vendor writes"]
    H -->|"approved actions"| J["✍️ adapter writes"]

    classDef blue fill:#2E71B8,stroke:#000,stroke-width:3px,color:#fff,font-weight:bold
    classDef mint fill:#4ECDC4,stroke:#000,stroke-width:3px,color:#000,font-weight:bold
    classDef amber fill:#FFE66D,stroke:#000,stroke-width:3px,color:#000,font-weight:bold
    classDef red fill:#DA5657,stroke:#000,stroke-width:3px,color:#fff,font-weight:bold
    classDef green fill:#C7F464,stroke:#000,stroke-width:3px,color:#000,font-weight:bold

    class A,B blue
    class C,D mint
    class E,F amber
    class G,H red
    class I,J green
```

| Layer | Role | Brutal truth |
|---|---|---|
| 🎮 **Cmdlets** | Operator surface | PowerShell objects in, PowerShell objects out. No GUI ceremony. |
| 🔌 **Adapters** | Vendor IO | NetSuite and HaloPSA specifics live at the edge, not smeared through sync logic. |
| 📦 **Canonical model** | Shared entity shape | Matching works against normalized `ExternalEntity` data instead of vendor-shaped chaos. |
| 🧠 **Matcher** | Decision support | Scores come with reasons. If it cannot explain the match, it does not pretend. |
| 📋 **Plan** | Change manifest | Sync becomes an Excel-reviewable artifact before it becomes vendor mutation. |
| 🧨 **Apply** | Controlled write path | `-Apply` is mandatory, `-WhatIf` is supported, `Review` rows are skipped. |

---

## 🎮 PowerShell API

| Cmdlet | Role |
|---|---|
| `Connect-EntitySyncVendor` | Configure and register a NetSuite or HaloPSA adapter; vendor-specific parameters appear after `-Vendor`. |
| `Get-EntitySyncConnection` | Inspect registered vendor connection objects. |
| `Test-EntitySyncConnection` | Validate adapter connectivity. |
| `Get-EntitySyncLookup` | Discover vendor lookup IDs such as HaloPSA top levels and N-central service organizations. |
| `Get-EntitySyncEntity` | Pull canonical entities from a connected vendor; `-Type` defaults to the selected vendor's supported entity type. |
| `New-EntitySyncPlan` | Compare source entities to target entities and emit a plan. |
| `Export-EntitySyncPlan` | Persist a plan to a reviewer-friendly `.xlsx` workbook or fallback JSON. |
| `Import-EntitySyncPlan` | Reload a reviewed `.xlsx` workbook or JSON plan. |
| `Invoke-EntitySyncPlan` | Apply approved `Link` / `Create` items with PowerShell safety semantics. |
| `Invoke-EntitySyncChain` | Generate or apply reviewed workbooks for NetSuite -> HaloPSA -> downstream vendor chains. |

### Flow

```mermaid
%%{init: {'theme': 'base', 'themeVariables': {'fontFamily': 'monospace', 'fontSize': '13px', 'primaryBorderColor': '#000', 'lineColor': '#000'}}}%%
graph LR
    A["🔌 Connect"] --> B["🔍 Get entities"]
    B --> C["📋 New plan"]
    C --> D{"Operator review"}
    D -->|"bad / ambiguous"| E["🛑 fix data first"]
    D -->|"approved"| F["💾 export artifact"]
    F --> G["🧪 Invoke -WhatIf"]
    G --> H["🚀 Invoke -Apply"]

    classDef blue fill:#2E71B8,stroke:#000,stroke-width:2px,color:#fff,font-weight:bold
    classDef mint fill:#4ECDC4,stroke:#000,stroke-width:2px,color:#000,font-weight:bold
    classDef amber fill:#FFE66D,stroke:#000,stroke-width:2px,color:#000,font-weight:bold
    classDef red fill:#DA5657,stroke:#000,stroke-width:2px,color:#fff,font-weight:bold
    classDef green fill:#C7F464,stroke:#000,stroke-width:2px,color:#000,font-weight:bold

    class A,B blue
    class C,D amber
    class E red
    class F,G,H green
```

### Excel Review

`Export-EntitySyncPlan` writes `.xlsx` files when the path ends in `.xlsx`. The visible `Review` worksheet is the operator surface; hidden workbook sheets preserve the full plan for safe round-trip import.

`-Path` accepts either a full file path or an existing directory. When it is a directory, the module generates a timestamped workbook name like `EntitySync-NetSuite-Customer-to-HaloPSA-Client-20260625-115250.xlsx`. `-FilePath` is an alias for users who prefer explicit file-path naming. Use `-PassThru` to return the created `FileInfo`.

Reviewers choose one `Decision` per row:

| Decision | Result on import |
|---|---|
| `Accept Planned` | Keeps the generated action and marks the item accepted. |
| `Reject` | Changes the item to `None`; apply skips it. |
| `Create` | Forces a target create. |
| `Link` | Forces a link/update of the target external/custom field. |
| `Update` | Forces an update of the matched target. |
| `No Update` | Changes the item to `None`; apply skips it without treating it as a rejection. |
| `Review` | Keeps the item blocked from apply. |

If the source was matched to the wrong target, choose the correct existing target from the `TargetName` dropdown. If `Decision` is blank or `Review`, importing the workbook changes that row to `Link`, assigns the selected target, marks the match as `ReviewerOverride`, and records a reviewer reason. A changed `TargetName` cannot be combined with `Create`, `Reject`, or `No Update`. If multiple targets have the same name, import fails with an ambiguity error rather than guessing.

`ReviewerNotes` are appended to the item's reasons during import, so the operator's decision context follows the plan into `-WhatIf` and apply output.

When HaloPSA is the target, `New-EntitySyncPlan` reads full client records by default so custom fields such as `CFNetSuiteCustomerID` are available for link detection. Use `-FullTargetObjects` only when you need HaloPSA site detail enrichment for address/postal matching; it adds another API call per client with a site.

Use `-ThrottleLimit` to cap parallel work. `0` means automatic/default. `-ThrottleLimit 1` forces sequential source/target reads, sequential matching, and one-at-a-time Halo full-object enrichment.

For faster HaloPSA planning, the adapter attempts to derive the numeric Halo custom field ID from `-HaloNetSuiteCustomerIdField`, then requests it from list reads with `include_custom_fields=<id>`.

You can still provide the field ID explicitly as an override:

```powershell
Connect-EntitySyncVendor -Vendor HaloPSA -HaloNetSuiteCustomerIdField CFNetSuiteCustomerID -HaloNetSuiteCustomerIdFieldId 123
```

If derivation fails and no explicit ID is configured, planning safely falls back to full client reads so link detection still sees `CFNetSuiteCustomerID`.

---

## 🧠 Matching Rules

`New-EntitySyncPlan` uses weighted matching instead of magical thinking.

| Signal | Why it matters |
|---|---|
| Existing external ID | Strongest signal. If a target is already linked, do not rematch by vibes. |
| Normalized name | Handles punctuation, casing, leading `The`, and legal suffix terms derived from [`cleanco`](https://github.com/psolin/cleanco)'s organization type database. |
| Address details | Helps separate same-name entities and messy subsidiaries. |
| Score thresholds | `-AutoLinkScore` defaults to `90`; `-ReviewScore` defaults to `70`. |
| Reasons | Plan items explain which evidence pushed the score up or down. |

Plan actions are intentionally boring:

| Action | Meaning |
|---|---|
| `None` | Already linked or no write required. |
| `Link` | Confident match; write the source external ID to the target. |
| `Create` | No credible target found and `-CreateMissing` was requested. |
| `Review` | Too risky. Human eyes required. The apply path skips it. |

---

## 🔐 Configuration

Pass credentials as parameters or environment variables. Secrets are used to connect; secrets are not written to plan files.

### HaloPSA

| Variable | Parameter |
|---|---|
| `HALO_BASE_URL` | `-HaloBaseUrl` |
| `HALO_CLIENT_ID` | `-HaloClientId` |
| `HALO_CLIENT_SECRET` | `-HaloClientSecret` |
| `HALO_NCENTRAL_INTEGRATION_ID` | `-HaloNCentralIntegrationId` |

The module requests a bearer token using HaloPSA client credentials. It tries `auth/token` first and falls back to `token`, matching Halo's client-credentials Postman collection. `-HaloScope` defaults to `all`.

Optional Halo controls: `-HaloTopLevelId`, `-HaloDefaultColour`, `-HaloNetSuiteCustomerIdField`, `-HaloNCentralIntegrationId`.

Halo client retrieval automatically pages through `api/client` with `pageinate`, `page_size`, and `page_no`. If the count is lower than the Halo UI, check `-IncludeInactive` and `-HaloTopLevelId`; both affect which clients are returned.

`Get-EntitySyncEntity` is fast by default and returns the Halo list payload. Use `-FullObjects` only when you need per-client detail/site address enrichment; that mode is intentionally slower and shows standard PowerShell progress.

Discover top-level IDs with:

```powershell
Get-EntitySyncLookup -Vendor HaloPSA -Type TopLevel
Get-EntitySyncLookup -Vendor HaloPSA -Type NCentralIntegration
Get-EntitySyncLookup -Vendor HaloPSA -Type NCentralIntegrationLink
Get-EntitySyncLookup -Vendor NCentral -Type ServiceOrganization
```

For `New-EntitySyncPlan -SourceVendor HaloPSA -TargetVendor NCentral`, HaloPSA's N-central integration client links are used as authoritative Halo client to N-central customer matches. If no Halo link exists, an N-central customer `externalId` equal to the HaloPSA client ID is treated as this workflow's owned link marker. First-class HaloPSA Site -> NCentral Site plans use HaloPSA `site_links` as authoritative site matches and parent client links to decide where missing N-central sites should be created.

Applying HaloPSA -> NCentral client plans maintains both sides of the client relationship. N-central creates use confirmed OpenAPI (`POST /api/service-orgs/{soId}/customers`), updates use EI2 SOAP `customerModify`, customer names sanitize `&` to `and`, and the adapter writes `externalId = <HaloPSA client ID>` plus organization custom properties for `HaloPSA Client ID`, `NetSuite Customer ID`, and `NetSuite Customer Name`. After a successful N-central write, HaloPSA `client_links` are upserted with `POST /api/ncentraldetails`. Site plans create N-central sites with `POST /api/customers/{customerId}/sites` and upsert HaloPSA `site_links`; N-central site field updates are no-op until a confirmed N-central site update endpoint is available.

### NetSuite

| Variable | Parameter |
|---|---|
| `NETSUITE_ACCOUNT_ID` | `-NetSuiteAccountId` |
| `NETSUITE_CONSUMER_KEY` | `-NetSuiteConsumerKey` |
| `NETSUITE_CONSUMER_SECRET` | `-NetSuiteConsumerSecret` |
| `NETSUITE_TOKEN_ID` | `-NetSuiteTokenId` |
| `NETSUITE_TOKEN_SECRET` | `-NetSuiteTokenSecret` |

NetSuite customer discovery uses standard REST Web Services SuiteQL, not a RESTlet. `NETSUITE_ACCOUNT_ID` should be the account ID only, for example:

```text
1234567
1234567_SB1
```

The adapter derives `https://<account>.suitetalk.api.netsuite.com/services/rest/query/v1/suiteql` from that account ID. The NetSuite role used by the token must have REST Web Services access and permissions to query customers with SuiteQL.

### N-central

| Variable | Parameter |
|---|---|
| `NCENTRAL_BASE_URL` | `-NCentralBaseUrl` |
| `NCENTRAL_USER_API_TOKEN` | `-NCentralUserApiToken` |
| `NCENTRAL_SERVICE_ORG_ID` | `-NCentralServiceOrgId` |
| `NCENTRAL_SOAP_USERNAME` | `-NCentralSoapUsername` |
| `NCENTRAL_SOAP_PASSWORD` | `-NCentralSoapPassword` |
| `NCENTRAL_SOAP_ENDPOINT_PATH` | `-NCentralSoapEndpointPath` |
| `NCENTRAL_SOAP_NAMESPACE` | `-NCentralSoapNamespace` |
| `NCENTRAL_HALOPSA_ID_PROPERTY_LABEL` | `-NCentralHaloPsaIdPropertyLabel` |
| `NCENTRAL_NETSUITE_ID_PROPERTY_LABEL` | `-NCentralNetSuiteIdPropertyLabel` |
| `NCENTRAL_NETSUITE_NAME_PROPERTY_LABEL` | `-NCentralNetSuiteNamePropertyLabel` |

N-central customer discovery and creation use the REST API. The adapter exchanges the User-API token at `/api/auth/authenticate`, reads customers from `/api/customers`, and creates customers with `/api/service-orgs/{soId}/customers`. For LISSTech N-central, pass `-NCentralServiceOrgId 50` or set `NCENTRAL_SERVICE_ORG_ID=50` before creating customers.

N-central customer updates and organization custom-property writes use EI2 SOAP at `dms2/services2/ServerEI2` by default. The custom property labels default to `HaloPSA Client ID`, `NetSuite Customer ID`, and `NetSuite Customer Name`; configure the labels if N-central uses different names.

You can set an existing N-central customer organization custom property directly when needed:

```powershell
Set-EntitySyncCustomProperty -Vendor NCentral -CustomerId 390 -Name 'HaloPSA Client ID' -Value 684 -Apply -WhatIf
```

This command updates existing custom-property values only; create the custom-property definitions in N-central first.

### LCAT

| Variable | Parameter |
|---|---|
| `LCAT_BASE_URL` | `-LCATBaseUrl` |
| `LCAT_BEARER_TOKEN` | `-LCATBearerToken` |

`Connect-EntitySyncVendor -Vendor LCAT` (`LTAC` is also accepted and normalizes to `LCAT`) registers a target-only adapter for syncing N-central Customer and Site records into LCAT customer scopes as one authoritative batch per approved plan, matching LCAT's `sync_ncentral_customers` RPC contract. `LCAT` has no customer-scope read endpoint, so plans never return target candidates — every N-central source plans as `Create`/`NoMatch`. Site-derived scopes carry their parent N-central customer's identifier as `ncentral_parent_customer_id`; a site with no parent N-central customer ID is blocked at plan time with `Action 'Review'` instead of being created. `LCATBearerToken` never appears in the returned connection object. See `specs/001-lcat-sync-adapter/spec.md`.

---

## 🧨 Safety Model

This module is built for vendor data, which means mistakes are expensive and embarrassing.

- 🔒 Discovery does not write.
- 📋 Planning does not write.
- 🧪 `Invoke-EntitySyncPlan` supports `-WhatIf`.
- 🧨 `Invoke-EntitySyncPlan` requires `-Apply` before writes are allowed.
- 🛑 `Review` items are skipped during apply.
- 💾 Plans can be exported and reviewed before mutation.
- 🧼 Credentials stay out of exported plans.

The intended workflow is **inspect → plan → review → dry run → apply**. Anything else is cowboy nonsense.

---

## 🔨 Build & Test

Requires [PowerShell 7.4+](https://learn.microsoft.com/powershell/), [.NET 8 SDK](https://dotnet.microsoft.com/download), [just](https://github.com/casey/just), and [Pester](https://pester.dev/) for tests.

```powershell
just              # list recipes
just build        # compile src/LISSTech.EntitySync.csproj into Module/
just test-load    # import the module and list exported commands
just test         # run Pester tests
just clean        # remove compiled output
```

---

## 📁 Project Structure

```text
📦 LISSTech.EntitySync
├── 📜 Module/
│   └── LISSTech.EntitySync.psd1        # module manifest; compiled DLL lands here
├── 📚 docs/                            # external help markdown
├── 🌎 en-US/                           # about topic source
├── 🧪 Tests/                           # Pester tests
├── 🧬 src/
│   ├── Adapters/                       # HaloPSA + NetSuite + N-central + LCAT vendor IO
│   ├── Commands/                       # public PowerShell cmdlets
│   ├── Core/                           # canonical models + plan types
│   ├── Mapping/                        # vendor-to-canonical mapping
│   ├── Matching/                       # weighted explainable matching
│   ├── Ports/                          # adapter abstractions
│   └── Runtime/                        # connection registry/runtime state
├── justfile                            # build/test automation
└── README.md
```

---

## 🧾 Status

Initial target: **NetSuite customers → HaloPSA clients**.

Also implemented: **N-central customers/sites → LCAT customer scopes**, applied as one authoritative batch per approved plan (see `specs/001-lcat-sync-adapter/`).

The core is intentionally vendor-neutral. Add the next vendor by implementing the adapter port, mapping into `ExternalEntity`, and leaving matching/planning alone.
