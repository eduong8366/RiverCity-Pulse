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

## Reconcile: mark, never delete, and refuse a truncated feed

Incremental runs only see rows whose `DateUpdated` moved, so they can't notice a request the city deleted, or an edit that didn't move `DateUpdated` past the watermark. The daily reconcile (03:30 Sacramento time) pages through every `ReferenceNumber` and `DateUpdated` the source serves and compares them with `dbo.service_request`.

- **Mark, don't delete:** a request missing from the source gets `source_removed_utc`, and keeps its history. The dashboard can leave it out, and if it comes back (a republish, a feed hiccup) the mark is cleared, by the reconcile or by any run that loads it again. A deleted row couldn't be told apart from one that never existed.
- **Fetch again what looks wrong:** keys we don't have, or whose source `DateUpdated` is newer than ours, are fetched by `ReferenceNumber IN (...)` and applied like any page. This is the safety net for the incremental watermark.
- **95% guard:** if the key count falls below 95% of the last successful reconcile's count, the run fails before it marks anything. A truncated response, a half-finished republish or a paging bug would otherwise mark hundreds of thousands of requests removed. The baseline is the last *successful* reconcile (before the first one, the cleaned table's count), so a failed run can't lower the bar for the next one. A real drop of more than 5% needs a person to look, which is the point.
- **Cost:** about 785 key-only pages (no geometry, three fields), a few minutes once a day.

## Data-quality checks compare with their own history

`DqRunner` writes `ops.dq_result` after every successful run. Each check picks a baseline that its signal can actually move:

- **Source count** against the highest count recorded in the last 7 days, not the previous run. Against the previous run a drop would fail once and then become the new normal; against a 7-day high it keeps failing until it's looked at or ages out. The feed grows about 1,500 rows a day, so a 2% drop is never normal growth.
- **Null rates** for requests created in the last 7 days against the 90 days before, not over the whole table. Over 1.57M rows, a week of blank addresses barely moves the overall rate; split by created date it shows up as a jump. Periods with fewer than 30 rows are recorded as Info rather than judged.
- **Flag counts** (sentinel and future dates, bad close dates, unmapped categories and sources, ...) against the previous check, warning on a jump of more than 1% (at least 50 rows). The counts themselves are recorded every run, so a slow drift is visible in `ops.dq_result` even when no single run warns.
- **DQ never fails a run:** checks run after the run is closed, and an error in them is logged, not thrown. Ingestion keeps the data current; DQ reports on it.

## Percentiles computed in C#, published by `usp_refresh_aggregates`

The API's medians and p90s are precomputed into `agg.*` after each run that changed rows, so every API call is a point lookup. The plan was to compute them in SQL with `PERCENTILE_CONT`. Measured on the live data (1.48M closed-request rows across the 30/90/365-day windows and their prior periods, 8 grouping sets of neighborhood × district × category) on SQL Server 2025 Express:

| Approach | Time |
|---|---|
| `PERCENTILE_CONT ... OVER (PARTITION BY ...)` over all grouping sets | ~124 s |
| `ROW_NUMBER` + `COUNT` interpolation, all sets in one sort | ~65 s |
| The same, one sort per grouping set | ~53 s |
| Opened/excluded counts alone with `GROUP BY GROUPING SETS` | ~12 s |
| **`AggregateBuilder` in C#: one streaming read, then in-memory sorts** | **~7.5 s, ~9 s with publish** |

Express caps the buffer pool and memory grants, so the window sorts spill. Refreshing every 15 minutes with a 1–2 minute query would hold the ingest lock most of the time.

- **Same definition:** `Percentile.Cont` is PERCENTILE_CONT's formula (value at position p × (n − 1), interpolated between neighbours). An integration test checks every cell against a hand-run `PERCENTILE_CONT` query, and the live refresh matched it for the cells checked by hand.
- **SQL still publishes:** the worker bulk-copies the results into `stg.agg_*`, and `usp_refresh_aggregates` swaps them into `agg.*` and records `agg.refresh` in one transaction. The API reads either the old set or the new one, never half of each.
- **When it runs:** after any successful run that inserted, updated, removed or restored rows, and at least once per Sacramento day even with no changes, because the windows end "today". It runs under the ingest lock, so two refreshes never share the staging tables. A failed refresh is logged and leaves the previous aggregates in place.
- **If this outgrows memory:** the builder holds one compact row per closed request in the last 730 days plus the open ones (about 25 MB today). The Developer edition (no Express caps) would make the SQL route viable again with only a connection-string change.

## Trend needs 30 requests per period and a 5% change

`trend` compares the current period's median days to close with the prior period's.

- **Fewer than 30 closed requests in either period gives a null trend.** The median of a handful of requests swings by days from one week to the next. A small neighborhood would otherwise flip between "slower" and "faster" on noise.
- **A change under 5% is "steady".** Medians are in fractional days, so almost every pair differs a little. The band keeps "slower" for changes someone would act on. `medianChangePct` is still returned, so a client can apply its own threshold.

## The API serves aggregates up to 5 minutes old

Every `/api` data endpoint is cached by OutputCache for 5 minutes, varying by the full query string. The aggregates change at most once per worker run (every 15 minutes), so the cache adds at most 5 minutes of staleness and turns repeated dashboard loads into memory reads. The worker can't evict the API's cache across processes; that was judged not worth a message bus. `/api/health/*` is never cached.

## Stale data is Degraded, not Unhealthy

`/api/health/ready` reports **Degraded (HTTP 200)** when no ingestion run has succeeded for 45 minutes, and **Unhealthy (503)** only when the database is unreachable. With stale data the API still serves the last good aggregates, and `asOf` on every response says how old they are. A load balancer shouldn't pull a working API out of rotation because the upstream feed is down. The freshness monitor planned for M5 is meant to alert on Degraded instead.
