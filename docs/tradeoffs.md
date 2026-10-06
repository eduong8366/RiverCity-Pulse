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

`raw.page` is written and committed before the page's apply transaction. If the apply fails, the payload is still on record for debugging, and the replay writes a second raw row for the same page. Pages are cheap (about 180 KB gzip), so a duplicate costs less than losing the evidence. `reclean` doesn't read these pages (see below), so duplicates never need sorting out.

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

## Reclean: fetch the feed again, write no history

The row hash covers raw values only, so a new cleaning rule doesn't create a history row for every request. The other side of that is that a changed cleaner or seed (say, a category moved to another group in `ref.category_map`) never reaches rows already loaded: the source row is the same, so `usp_apply_batch` counts it as unchanged. `worker reclean` closes that gap.

- **Fetch, not replay:** the plan was to replay `raw.page` through the current cleaners. But incremental pages are pruned after 180 days, so for most requests the only raw copy left is from the backfill, and replaying it would put old versions back. Fetching the whole feed again gives the current version of every request, and it reuses the backfill path unchanged: keyset paging, a checkpoint per page (a killed reclean resumes), the ingest lock, the run log (pipeline `Reclean`). The cost is about 785 pages, about 8 minutes on this machine, and it only runs when someone changes a rule.
- **Reclean mode in `usp_apply_batch`:** with `@reclean = 1`, a row whose hash matches but whose cleaned columns differ (compared with `IS DISTINCT FROM`, so NULLs count) has its cleaned columns rewritten. `row_hash`, `object_id` and `last_changed_utc` stay as they are, because the source didn't change. These rows count in `ops.ingest_run.rows_recleaned`, apart from `rows_updated`, which keeps meaning "the source changed". A request edited at the source during the reclean is an ordinary update, with its history row.
- **No history rows:** history records versions the source served. A reclean doesn't produce a new version, so it adds no history row, and older history rows keep the cleaning they were stored with.
- **Labels are not cleaning:** the clear-out bit in `dq_flags` is set later by `usp_classify_for_metrics`, so the comparison ignores it and the update keeps it. Otherwise every clear-out row would count as recleaned on every run.
- **Requests gone from the source keep their old cleaning.** The reclean only sees what the source serves today. Rows marked with `source_removed_utc` have no current version to clean, and they stay out of the figures anyway.
- **Always refreshes:** a reclean ends with an aggregate refresh even when nothing changed, because a seed edit such as `ref.non_service_type` changes no cleaned row and shows only in the classification the refresh runs.
- **Raw pages:** a finished reclean deletes the previous reclean's raw pages, keeping one full copy of the feed as it was served.

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

## "What's getting slower" ranks by days added, and shows clear-outs beside it

`/api/trends/slower` lists the cells whose `trend` is "slower" (the rule above), most days added to the median first.

- **Days, not percent.** A median going from 0.5 to 1.5 days is +200%, and from 20 to 40 days is +100%; the second is the one a resident notices. Percent is still returned, and breaks ties.
- **Clear-outs stay in, and each row says how many.** Clear-outs are counted as recorded ([`metrics.md`](metrics.md)), so a neighborhood where old Parking requests were closed in bulk tops the list (on 2026-10-04: Pell/Main Industrial Park, 7.9 → 704.7 days, 83 of its 129 closures in clear-outs). Leaving them out of this list alone would give it a second definition; the panel shows the count next to each row instead, and says what it may mean.
- **The counts come with it.** `compared` says how many neighborhoods were slower, steady, faster or too small to compare, so "10 slower" can be read against "76 of 129".

## The end-to-end tests run the real API, not mocked responses

`Sac311.E2E.Tests` (Playwright for .NET, xUnit) starts the API on Kestrel (`WebApplicationFactory.UseKestrel`) over the same seeded database as the API tests, and serves the built Angular app from the same origin through a startup filter, as the dev proxy does in development. The browser therefore exercises the whole path: SQL, aggregates, endpoints, the app's URL state and rendering, and the hand-worked figures in the assertions are the same ones the API tests check.

- **Why not Playwright's Node runner with mocked `/api` routes:** it would test the app against fixtures that can drift from the API, and add a second test runner. The project's tooling is .NET, so the browser tests are too.
- **Cost:** the e2e job needs SQL Server, Node (to build the app) and Chromium, so it is a separate CI job, and the .NET job skips it (`Category!=E2E`). Locally `dotnet test` runs it after `npm run build` in `web/`; the host downloads Chromium on first use (`Microsoft.Playwright.Program.Main(["install", "chromium"])`, so no script).

## The API serves aggregates up to 5 minutes old

Every `/api` data endpoint is cached by OutputCache for 5 minutes, varying by the full query string. The aggregates change at most once per worker run (every 15 minutes), so the cache adds at most 5 minutes of staleness and turns repeated dashboard loads into memory reads. The worker can't evict the API's cache across processes; that was judged not worth a message bus. `/api/health/*` is never cached.

## Stale data is Degraded, not Unhealthy

`/api/health/ready` reports **Degraded (HTTP 200)** when no ingestion run has succeeded for 45 minutes, and **Unhealthy (503)** only when the database is unreachable. With stale data the API still serves the last good aggregates, and `asOf` on every response says how old they are. A load balancer shouldn't pull a working API out of rotation because the upstream feed is down. The freshness monitor alerts on Degraded instead (next section).

## Schema drift degrades readiness at once; the monitor lives in the API

- **Two Degraded causes, two clocks.** `freshness` waits 45 minutes, because one failed run is usually a network blip the next run fixes. `ingestion` degrades as soon as the newest finished run is `SchemaDrift`: every later run checks the same contract and stops the same way, so waiting would only delay the alert. The [schema drift drill](incidents/2026-10-04-schema-drift-drill.md) found that without this check, drift would have gone 45 minutes without anyone being told. A `Failed` run doesn't degrade `ingestion`, or a transient error would page someone for something the scheduler fixes 15 minutes later.
- **The monitor is an `IHealthCheckPublisher` in the API**, not a timer in the worker. It runs exactly the checks `/api/health/ready` runs, so the alert and the endpoint can't disagree, and it still fires when the worker process has died, which is the case a worker-side monitor would miss. The cost: if the API is down there is no alert either, but then readiness is unreachable too and any external uptime check catches it.
- **It alerts on changes, not on every check.** One alert when readiness changes (and one at startup if it isn't Healthy), so a drift that lasts all night sends two messages, not 600. A change of cause while still Degraded (drift, then also stale) doesn't alert again; the alert text and `/api/health/ready` list every failing check.
- **A generic webhook, not a vendor SDK.** The alert is a JSON POST with a `text` field that Slack-style incoming webhooks display as is; the other fields (status, previous status, each check) are there for anything that parses them. With no URL configured, alerts are only logged.

## The nightly source check keeps its baseline in the repo

The alert monitor needs the API running and ingestion needs the worker running; on a machine that is off, a change at the source would go unnoticed until the next run. [`live-contract.yml`](../.github/workflows/live-contract.yml) runs `worker verify-source --check` on GitHub every night to cover that gap.

- **Baseline in `docs/source-profile.md`, not a stored count.** The check compares the live count with the profile's "Row count" line, so the workflow needs no database, no secret and no state carried between runs (an artifact or a cache that expires, or a commit from CI). The cost is a chore: the feed grows about 1,500 rows a day, so the profile has to be rerun and committed every month or two, or the check fails on growth alone. The failure says which way the count moved, and growth past 5% names the fix.
- **±5% here, 2% in the DQ check.** The DQ source-count check compares with a 7-day high and fails on a 2% drop, because it runs after every ingestion run against a recent baseline. This check's baseline is weeks old, so it allows normal growth and catches what matters at night: a feed that was emptied, truncated or republished, or one that suddenly gained rows.
- **The same contract as ingestion.** It calls `SchemaContract.Check`, so it fails on exactly the drift that would stop the next run as `SchemaDrift`; added fields are logged, not failed.
- **Only on a schedule.** A change at the city isn't a defect in a pull request, so it doesn't block merges; CI stays about the code.

## Metric classification runs before each refresh, in SQL

Headline figures leave out non-service requests and requests with date problems, and clear-outs of old requests are counted but labelled for the published notes ([`metrics.md`](metrics.md)). Neither the non-service classification nor the clear-out label is decided by the per-row cleaners. `usp_classify_for_metrics` sets them at the start of every aggregate refresh instead.

- **Why not in the cleaners:** both rules depend on more than one row. Non-service depends on the ref maps, which can change without the row changing. A clear-out depends on how many other requests closed that day in the category, or that minute across categories. A cleaner sees one row at a time, so it can't apply either rule.
- **Why before every refresh:** `usp_apply_batch` rewrites `dq_flags` on a changed row, which clears the clear-out bit, and a seed edit changes which rows are non-service. A run that changed rows always refreshes, and the refresh classifies first, so the aggregates and notes never read a stale classification. A seed edit takes effect at the next refresh, with no reclean. The proc writes only rows whose value changes: a rerun on the live data changes 0 rows and takes about 8 s.
- **Refresh before DQ:** jobs refresh the aggregates before running the data-quality checks, so the `flag.BulkClosure` count DQ records is the current one. A new clear-out shows up as a flag-count warning on the run that brought it.
- **Non-service is a column, not a DQ flag:** an information call isn't bad data. `is_service` keeps "what kind of record this is" apart from "is this record's data trustworthy", and it is left out of every figure, while DQ flags only affect timing.
- **The map key lives in C# and in SQL:** the seeds are keyed by `MapKey.For`, while the classification runs in SQL over raw `category_level1`/`category_level2`. `dbo.fn_map_key` (`REGEXP_REPLACE`, SQL Server 2025) is the SQL copy, and an integration test compares the two on awkward inputs. It runs over the few hundred distinct category pairs, not the 1.57M rows. The alternative was storing both keys on every row, which would have needed a reclean and two more columns to keep in sync.
- **The exclusion breakdown and the notes are plain SQL:** `/api/meta/exclusions` and `/api/meta/clear-outs` need counts and averages, not percentiles, so `usp_build_exclusions` and `usp_build_clear_outs` compute them with `GROUP BY` instead of growing `AggregateBuilder`. They use the builder's period boundaries, and integration tests check them against hand counts. The note sentence is generated by the API from the row, so its wording can change without a refresh.
- **Rule values are code, not configuration:** `BulkClosureRule` (100 closures older than 180 days in a day and category, or 50 in one minute across categories; members older than 90) is passed to the procs as parameters. A setting someone could tune per deployment would make the published notes depend on who ran them.
- **Cost:** a refresh takes about 20 s on the live data (classify ~8 s, build ~5 s, publish with exclusions and notes ~6 s). That is still small next to the 15-minute schedule.
- **History rows are left alone:** both rules describe a request type or a batch of closures, not a version of a request.

## Clear-outs are counted as recorded, with notes (method change, 2026-10-04)

Session 7b first left bulk clear-outs out of the median and p90. Hours later that was reversed, and they are now counted and annotated ([`metrics.md`](metrics.md#method-change-2026-10-04) has the before/after figures).

- **Why counted:** the requests really did sit open that long in the city's records. Every exclusion option measured made the city look faster: the 7b rule moved the 90-day median from 5.89 to 3.88 days and the p90 from 328 to 37.6. No count of closures proves a clear-out wasn't real work, and the 100-closure cutoff treated identical Parking batches of 99 and 100 differently. A "50 or more, plus same-minute sweeps" exclusion was also measured (3.81 / 35.4) and rejected for the same reason. Overstating a delay is a defensible error for a public dashboard; understating it isn't.
- **Why notes:** a reader seeing Parking's 533-day median needs the reason next to the number. Notes are generated by the published rule, stated as facts and shown where the number is. They are never picked by hand and never say "ignore this", so they explain the figure without changing it.
- **Why the sweep clause:** the label can't change any figure, so a broader rule costs nothing in honesty. Two cross-department minutes (2026-08-21 and 2026-09-03) were only partly caught by the day clause, and the sweep clause labels all of them.
- **Why non-service stays out:** putting it back would *flatter* the city (information calls pull the median to 2.02 days) and nearly double the backlog with unread inbox items. Information contacts are reported separately with counts instead.
- **Symmetry:** the mirror case (batches of create-and-close records in a service category) was looked for with the same thresholds and not found. A note kind would be added for it by the same approach if one appeared.
- **Not a toggle:** there is no "as recorded" switch on the dashboard. The job is to publish one definition, not to let the reader pick the friendlier number.

## Dashboard: no basemap, boundaries from the API, ECharts loaded late

- **No basemap under the choropleth.** The 129 neighborhood shapes carry the page on their own, and the river shows as the gap between them. A tile layer would make the page depend on a third-party service for little gain: CARTO's free tiles now need an API key, and OpenStreetMap's tile servers aren't meant for an app's traffic. Leaflet stays for zoom, hover and keyboard focus on each shape.
- **The API serves the boundaries.** Angular only serves assets from inside `web/`, and a copy of the GeoJSON there would be a second file to keep in step. `/api/geo/neighborhoods` embeds `data/geo/sacramento-neighborhoods.geojson` at build and adds each feature's slug with the same `Neighborhood.Slug` the aggregates use. The map joins on that one key, so no slug code is ported to TypeScript.
- **Quantile bins are computed in the browser** from the map response, interpolated like `PERCENTILE_CONT`; they reproduce the cuts in [`design/decision.md`](design/decision.md). Medians run from about 1 to 700 days, so equal-width bins would put almost every neighborhood in the first one.
- **ECharts is loaded with `import()`** when the chart first renders. The initial bundle stays at about 100 kB transferred, and ECharts follows as a separate chunk of about 160 kB.
- **Filters live in the URL**, updated with `replaceState`. A view can be shared, and Back leaves the page instead of undoing one click at a time.

