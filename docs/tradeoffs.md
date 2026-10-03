# Tradeoffs

Design choices that had a reasonable alternative, and why this project went the way it did.

## UPDATE + INSERT instead of MERGE in `usp_apply_batch`

`dbo.usp_apply_batch` applies each staged page with separate statements: insert new history versions, touch unchanged rows, update changed rows, insert new rows.

- **Why not MERGE:** SQL Server's MERGE has a long list of known bugs (wrong results with filtered indexes, assertion failures, race conditions without `HOLDLOCK`). This table has a filtered index on open requests.
- **Concurrency isn't a reason to need MERGE:** ingestion jobs run one at a time (the `sac311-ingest` applock arrives with the scheduler in M2), so `usp_apply_batch` is the only writer.
- **Exact counts:** each statement's `@@ROWCOUNT` is one of inserted, updated, unchanged or history. MERGE would need an `OUTPUT $action` table to get the same numbers.
- **Cost:** the `#batch` table is joined to `dbo.service_request` three times per page of 2,000 rows. On the unique `reference_number` index that is cheap.

## Stale versions don't overwrite newer ones

A replayed or out-of-order page can carry an older version of a request. The changed-row UPDATE only applies when the staged `updated_utc` is at least the stored one, so the stored row never goes back in time. The older version still gets a history row if its hash is new, because history records every distinct version seen.

## Raw page written before the apply transaction

`raw.page` is written and committed before the page's apply transaction. If the apply fails, the payload is still on record for debugging, and the replay writes a second raw row for the same page. Pages are cheap (about 180 KB gzip), so a duplicate costs less than losing the evidence. `reclean` has to take the newest raw row per cursor.
