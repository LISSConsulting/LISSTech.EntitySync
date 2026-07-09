> Project working memory for Ralph agents.
> Keep this file compact: unresolved state only, not a changelog. Completed history belongs in git, releases, and .ralph/logs/*.jsonl.

## Current Focus

- Active spec: `specs/001-lcat-sync-adapter/`.
- Spec tasks T001-T053 are complete. The current queue is empty; continue with the Empty Queue workflow unless a new spec task, blocker, or finding appears.
- Empty Queue sweep done: LCAT planning now blocks duplicate derived customer-scope slugs for review; `just test` passes.
- Empty Queue sweep done: LCAT adapter now rejects malformed customer-scope batch rows before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT apply and adapter paths now reject duplicate customer-scope slugs before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT apply and adapter paths now reject case-only duplicate N-central customer-scope IDs before HTTP send; `just test` passes.
- Empty Queue sweep done: LCAT planning now blocks non-NCentral source records before apply; `just test` passes.
- Empty Queue sweep done: LCAT adapter now trims customer-scope request values before validation and serialization, blocking whitespace-hidden duplicate N-central IDs before HTTP send; `just test` passes.

## Open Blockers

| Priority | Blocker | Evidence | Next Action |
|----------|---------|----------|-------------|

## Open Findings

| Priority | Finding | Evidence | Status |
|----------|---------|----------|--------|

## Decisions To Preserve

-
