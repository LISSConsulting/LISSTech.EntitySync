# NetSuite SuiteQL Pagination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return every bounded NetSuite SuiteQL row instead of silently stopping at the first 1,000-row REST page.

**Architecture:** `NetSuiteEntityAdapter` will own one metadata-validating offset pager. Callers consume each `JsonElement` while its page document is alive, avoiding element cloning and allowing customer, raw-row, and address mappings to share transport behavior without extra page-sized copies.

**Tech Stack:** C# 12, .NET 8, `HttpClient`, `System.Text.Json`, xUnit

## Global Constraints

- Keep planner limits, scheduler policy, matching, apply behavior, and public command signatures unchanged.
- Use OAuth-signed `limit` and `offset` requests with a maximum REST page size of 1,000.
- Keep SuiteQL text stable across pages and order customer rows by `entityid, id`.
- Fail closed on inconsistent or non-advancing metadata; never return a partial entity set.
- Preserve existing raw-array and short object-response compatibility.

---

### Task 1: Complete SuiteQL Page Retrieval

**Files:**
- Create: `Tests/LISSTech.EntitySync.Platform.Tests/NetSuitePaginationTests.cs`
- Modify: `src/Adapters/NetSuite/NetSuiteEntityAdapter.cs`

**Interfaces:**
- Consumes: `EntityQuery.Count`, NetSuite response fields `items`, `count`, `offset`, `totalResults`, and `hasMore`.
- Produces: unchanged public `GetEntitiesAsync` and `InvokeSuiteQlAsync` behavior, now backed by `ReadSuiteQlPagesAsync(string suiteQl, int? maximumItems, Action<JsonElement> consume, CancellationToken cancellationToken)`.

- [ ] **Step 1: Add a failing multi-page customer regression**

Create an xUnit test with a scripted `HttpMessageHandler`. Return 1,000 customer rows with `hasMore: true`, followed by one row with `hasMore: false`, then empty address responses. Call:

```csharp
var entities = await adapter.GetEntitiesAsync(new EntityQuery
{
    EntityType = "Customer",
    IncludeInactive = true,
    Count = 1001
}, default);

Assert.Equal(1001, entities.Count);
Assert.Equal(Enumerable.Range(1, 1001).Select(value => value.ToString()), entities.Select(entity => entity.Id));
Assert.Equal("?limit=1000&offset=0", handler.Requests[0].RequestUri!.Query);
Assert.Equal("?limit=1&offset=1000", handler.Requests[1].RequestUri!.Query);
```

The handler must capture each URI/body and return fresh `HttpResponseMessage` instances. Add an internal adapter constructor accepting `HttpMessageHandler`; route it through `VendorHttpClientFactory.Create(..., minimumRequestInterval: TimeSpan.Zero)` so tests retain production response bounds without a 500 ms delay.

- [ ] **Step 2: Add failing metadata-validation regressions**

Add tests proving:

```csharp
await Assert.ThrowsAsync<InvalidOperationException>(() =>
    adapter.InvokeSuiteQlAsync("SELECT id FROM customer", default));
```

for a response whose `count` differs from `items.Length`, and proving a full 1,000-row object response without pagination metadata fails as ambiguous. Assert the messages name pagination metadata, not response payloads or credentials.

Also extend the query-order assertion:

```csharp
Assert.Contains("ORDER BY entityid, id", NetSuiteEntityAdapter.BuildCustomerQuery(query));
```

- [ ] **Step 3: Run the focused tests and confirm red state**

Run:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --filter 'FullyQualifiedName~NetSuitePaginationTests|FullyQualifiedName~SuiteQlSearchEscapesLiteralWildcardCharacters' --verbosity minimal
```

Expected: pagination tests fail because only the first response is consumed and metadata is ignored.

- [ ] **Step 4: Implement the shared pager**

In `NetSuiteEntityAdapter`:

```csharp
private const int SuiteQlPageSize = 1000;

private async Task ReadSuiteQlPagesAsync(
    string suiteQl,
    int? maximumItems,
    Action<JsonElement> consume,
    CancellationToken cancellationToken)
```

For each page, choose `limit = Math.Min(SuiteQlPageSize, maximumItems - consumed)` when bounded, build `/services/rest/query/v1/suiteql?limit={limit}&offset={offset}`, and send the unchanged JSON body through `RateLimitedHttpRequester`. Parse and consume rows before disposing the page document.

Require all four metadata fields together when present. Validate returned `count`, requested `offset`, stable `totalResults`, consistent `hasMore`, nonempty advancing pages, and the caller bound. Treat a raw array as one terminal compatibility page. Treat a short object without metadata as terminal; reject a metadata-free object containing exactly the requested page size.

Replace the one-page readers in `GetEntitiesAsync`, `InvokeSuiteQlAsync`, and `AddCustomerAddressesAsync` with this method. Keep address grouping incremental inside the consume callback. Remove the obsolete one-page `ExecuteSuiteQlAsync` method.

Update `BuildCustomerQuery` to:

```csharp
sql.Append(" ORDER BY entityid, id");
```

Every URI must be passed to `BuildOAuthHeader`, so each page and retry signs its exact `limit` and `offset` parameters.

- [ ] **Step 5: Run focused tests and confirm green state**

Run the Step 3 command.

Expected: all selected tests pass with 1,001 unique ordered entities and validated request URIs.

- [ ] **Step 6: Commit the implementation**

```bash
git add src/Adapters/NetSuite/NetSuiteEntityAdapter.cs Tests/LISSTech.EntitySync.Platform.Tests/NetSuitePaginationTests.cs Tests/LISSTech.EntitySync.Platform.Tests/HardeningTests.cs
git commit -m "fix: page NetSuite SuiteQL results"
```

### Task 2: Repository Verification

**Files:**
- Verify only; modify production or test files only if a failure exposes a defect in Task 1.

**Interfaces:**
- Consumes: the completed NetSuite pager.
- Produces: build and test evidence for merge.

- [ ] **Step 1: Build the module**

```bash
just build
```

Expected: exit 0 with zero compilation errors.

- [ ] **Step 2: Run platform tests**

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Tests/LISSTech.EntitySync.Platform.Tests/LISSTech.EntitySync.Platform.Tests.csproj --configuration Release --no-restore --verbosity minimal
```

Expected: all platform tests pass.

- [ ] **Step 3: Run module tests**

```bash
just test
```

Expected: all applicable Pester tests pass. If the known macOS-only DPAPI baseline remains unavailable, report that exact unrelated limitation rather than replacing the test.

- [ ] **Step 4: Review the final diff**

Confirm no public API, planner limit, scheduler policy, matching, or apply semantics changed. Confirm errors contain no URI query values, response bodies beyond the existing bounded preview, OAuth parameters, or credentials.

- [ ] **Step 5: Merge and verify main**

Merge the feature branch into `main`, rerun the focused pagination test on the merged tree, and report commit SHAs plus exact test counts.
