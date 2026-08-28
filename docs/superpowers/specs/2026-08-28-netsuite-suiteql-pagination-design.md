# NetSuite SuiteQL Pagination Design

## Problem

`NetSuiteEntityAdapter` sends one SuiteQL REST request without `limit` or `offset`. NetSuite returns 1,000 rows by default, while the adapter reads only `items` and discards pagination metadata. Plans therefore silently contain at most the first 1,000 NetSuite customers even when the planner requests up to 5,001 rows.

## Decision

Implement REST pagination inside `NetSuiteEntityAdapter`. Keep the SuiteQL statement stable and issue OAuth-signed requests with `limit` and `offset` until the requested count is reached or NetSuite reports no further results.

A shared page reader will serve customer retrieval, customer-address enrichment, and raw SuiteQL execution. Customer queries will order by `entityid, id` so offset pagination has a deterministic unique tie-breaker.

## Data Flow

1. Use NetSuite's 1,000-row REST page size on every request so each advancing offset remains evenly divisible by the limit.
2. POST the unchanged SuiteQL body to `/services/rest/query/v1/suiteql?limit=1000&offset={offset}`.
3. Parse the response `items` plus `count`, `offset`, `totalResults`, and `hasMore` metadata.
4. Append only the caller's remaining requested rows.
5. Advance by the returned item count and repeat while more rows are reported.
6. Return the complete bounded result to the existing mapper and planner.

Raw-array responses remain a terminal one-page compatibility form and are consumed in full up to the caller's actual bound. Object responses without pagination metadata are accepted only when they contain fewer than 1,000 rows; a full ambiguous page fails closed rather than silently truncating.

## Invariants and Errors

- Response `items` must be an array.
- Reported `count` must equal the number of returned items.
- Reported `offset` must equal the requested offset.
- `totalResults` must be nonnegative and stable across pages.
- A page that reports more results must contain at least one item.
- Pagination must strictly advance and never return more than the caller requested.
- Any malformed or inconsistent response throws a redacted `InvalidOperationException`; no partial entity set reaches planning.
- Every page and every rate-limit retry receives a fresh OAuth signature for its exact URI.

## Scope

The change is confined to the NetSuite adapter and its behavioral tests. Planner limits, scheduler policy, matching, apply behavior, and public command signatures remain unchanged.

## Verification

Behavioral tests will prove:

- A 1,300-row customer result is assembled from two REST pages without duplicates or omissions.
- Both requests use the fixed 1,000-row REST limit and advancing offsets that remain divisible by that limit.
- Requested counts below one page stop without an extra request.
- Missing, present-but-invalid, inconsistent, and non-advancing pagination metadata fail closed.
- Terminal raw-array compatibility responses preserve every row up to the caller's actual bound.
- Existing OAuth, rate-limit retry, query escaping, build, and platform tests remain green.
