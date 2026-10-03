# Tradeoffs

Design choices that had a reasonable alternative, and why this project went the way it did.

## UPDATE + INSERT instead of MERGE in `usp_apply_batch`

`dbo.usp_apply_batch` applies each staged page with separate statements: insert new history versions, touch unchanged rows, update changed rows, insert new rows.

- **Why not MERGE:** SQL Server's MERGE has a long list of known bugs (wrong results with filtered indexes, assertion failures, race conditions without `HOLDLOCK`). This table has a filtered index on open requests.
- **Concurrency isn't a reason to need MERGE:** ingestion jobs run one at a time under the `sac311-ingest` applock (below), so `usp_apply_batch` is the only writer.
- **Exact counts:** each statement's `@@ROWCOUNT` is one of inserted, updated, unchanged or history. MERGE would need an `OUTPUT $action` table to get the same numbers.
- **Cost:** the `#batch` table is joined to `dbo.service_request` three times per page of 2,000 rows. On the unique `reference_number` index that is cheap.

## Stale versions don't overwrite newer ones

A replayed or out-of-order page can carry an older version of a request. The changed-row UPDATE only applies when the staged `updated_utc` is at least the stored one, so the stored row never goes back in time. The older version still gets a history row if its hash is new, because history records every distinct version seen.

## Raw page written before the apply transaction

`raw.page` is written and committed before the page's apply transaction. If the apply fails, the payload is still on record for debugging, and the replay writes a second raw row for the same page. Pages are cheap (about 180 KB gzip), so a duplicate costs less than losing the evidence. `reclean` has to take the newest raw row per cursor.

## The ingest lock is a session applock on its own unpooled connection

Every job takes `sp_getapplock 'sac311-ingest'` (exclusive, timeout 0) before it writes anything, and a job that can't get it is recorded as `Skipped`.

- **Session owner, not transaction owner:** the pipeline commits once per page, so a transaction-owned lock would be released after the first page.
- **Its own connection:** the lock connection stays open for the whole job while pages are written on other connections. If the process is killed, SQL Server ends the session and releases the lock, so there is never a stale lock to clear by hand.
- **Unpooled:** a pooled connection goes back to the pool with its session still alive. If the explicit release ever failed, that pooled session would keep holding the lock.
- **Abandoned runs:** a killed process leaves its `ops.ingest_run` row as `Running`. The next job closes such rows as `Failed` right after taking the lock, since while it holds the lock no other run can be in progress.

## Incremental watermark: newest DateUpdated seen, moved only on success

- **Data time, not clock time:** the watermark is the newest `DateUpdated` loaded, so clock skew between this machine and ArcGIS can't open a gap. It is capped at the run's start, so a future-dated row can't push it ahead of real edits.
- **No per-page checkpoint:** incremental runs are 1–3 pages, so a killed run just reruns from the old watermark, and replayed rows come back as unchanged.
- **60-minute overlap:** each run starts 60 minutes before the watermark. This covers a row edited behind the run's OBJECTID cursor while it was paging, and a source that stamps `DateUpdated` late. The cost is about 100 unchanged rows per run.
- **Seeded by the backfill:** a finished backfill sets the watermark to the time it *first* started (a resume keeps the original start), or its `--until` if earlier, less the overlap. It only moves an existing watermark backward, never forward: a `--since` slice doesn't cover the gap between an old watermark and its start date, and an earlier watermark only costs a longer, idempotent run.
