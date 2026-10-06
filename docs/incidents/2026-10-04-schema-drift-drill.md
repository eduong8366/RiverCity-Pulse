# Incident drill: the source renames `PublicStatus` (2026-10-04)

**Type:** planned drill, run against the local database with the live feed behind a proxy.
**Duration:** 33 seconds of drift (21:11:08 to 21:11:41 Sacramento time; 04:11:08 to 04:11:41 UTC on 2026-10-05).
**Impact:** none. No rows written, the incremental watermark held, the dashboard kept serving the last good figures.
**Detected by:** the readiness check `ingestion`, 6 seconds after the failed run, through the alert webhook.

## Scenario

The city's ArcGIS layer is edited by people we don't talk to. The most damaging change we can imagine that still returns HTTP 200 is a renamed field. If `PublicStatus` became `Status`, a pipeline that read it by name would load every request with a null status: open and closed counts, the backlog and every median would quietly go wrong.

The pipeline is meant to refuse instead. Every run first fetches `layer?f=json` and compares the fields with `SchemaContract`. On a mismatch it records the run as `SchemaDrift`, writes nothing and exits with code 3. This drill checks the whole chain end to end: **SchemaDrift → no writes → health Degraded → alert → recovery.**

## Setup

[`tools/FakeArcGis`](../../tools/FakeArcGis/Program.cs) is a WireMock.Net server that proxies the live layer. It serves `layer?f=json` with `PublicStatus` renamed to `Status` while drift is on, and passes `query` calls straight through to the city, so the worker sees real data. It also takes the API's alert webhook, so the drill needs no outside service.

```console
$ dotnet run --project tools/FakeArcGis                      # drift on, port 5280
$ Alerts__WebhookUrl=http://localhost:5280/drill/alerts Alerts__Period=00:00:15 \
  dotnet run --project src/Sac311.Api                        # monitor runs every 15 s instead of 60
$ ArcGis__BaseUrl=http://localhost:5280/54falWtcpty3V47Z/arcgis/rest/services/SalesForce311_View/FeatureServer/0 \
  dotnet run --project src/Sac311.Worker -- incremental      # the run under test
$ curl -X POST http://localhost:5280/drill/heal              # the "fix"
```

Before the drill, a normal incremental against the live layer (run 19) brought the data up to date, so readiness started Healthy.

## Timeline (UTC, 2026-10-05)

| Time | Event |
|---|---|
| 04:10:14 | Run 19 (Incremental, live layer) Succeeded: 312 fetched, 164 inserted, 80 updated. Watermark 04:09:00. Readiness Healthy. |
| 04:11:08 | Run 20 (Incremental, through the fake) starts and fetches the layer description. The worker logs `FTL Schema drift, stopping without writing: missing PublicStatus; added Status` and exits with code 3. `ops.ingest_run` row 20: status `SchemaDrift`, 0 fetched, error `Schema drift: missing PublicStatus; added Status`. No `query` call reached the fake. |
| 04:11:14 | The API's monitor runs the readiness checks: `ingestion` is Degraded and overall readiness goes Healthy → Degraded. The API logs `ALERT RiverCity Pulse readiness is Degraded (was Healthy).` and POSTs it to the webhook; the fake prints `ALERT RECEIVED`. `freshness` is still Healthy (the last success was 1 minute earlier). |
| 04:11:39 | Drill "fix": `POST /drill/heal`, so the layer description matches the contract again. |
| 04:11:40 | Run 21 (Incremental, through the fake) Succeeded: 39 fetched, 0 inserted, 1 updated, 38 unchanged, 1 history row. Watermark 04:09:00 → 04:11:00. |
| 04:11:43 | Monitor: readiness Healthy. Alert `RiverCity Pulse recovered: readiness is Healthy again.` received by the webhook. |
| 04:12:01 | Run 21 finishes its aggregate refresh (classification 7.8 s, refresh 11.1 s) and DQ: 22 checks, 0 failed, 0 warned. |

## Evidence: nothing was written

Snapshot query before run 20 and after it:

| | Before run 20 | After run 20 | After run 21 |
|---|---|---|---|
| `dbo.service_request` rows | 1,575,804 | 1,575,804 | 1,575,804 |
| `dbo.service_request_history` rows | 1,577,829 | 1,577,829 | 1,577,830 |
| `raw.page` rows | 898 | 898 | 899 |
| Incremental watermark | 04:09:00 | 04:09:00 | 04:11:00 |
| Latest `agg.refresh` | 7 | 7 | 8 |

The drifted run touched no table except its own `ops.ingest_run` row. Because the watermark didn't move, the recovery run asked for `DateUpdated > 03:09:00` (the watermark minus the 60-minute overlap) and got everything edited during the drift: 38 rows it already had (unchanged) and 1 real edit made after run 19. Nothing was lost, and nothing needed a backfill.

The readiness response during the incident:

```json
{"status":"Degraded","checks":[
  {"name":"database","status":"Healthy","description":"The database answers."},
  {"name":"freshness","status":"Healthy","description":"Last successful run 1 min ago (limit 45 min)."},
  {"name":"ingestion","status":"Degraded","description":"Last run 20 (Incremental) stopped on schema drift and wrote nothing. Schema drift: missing PublicStatus; added Status",
   "data":{"runId":20,"pipeline":"Incremental","status":"SchemaDrift","startedUtc":"2026-10-05T04:11:08.654Z"}}]}
```

## What the drill changed

Planning the drill showed that until this session, schema drift would have taken **45 minutes** to show up anywhere outside the worker's log. Readiness only checked the database and freshness, and freshness only goes stale when no run has succeeded for 45 minutes. Each 15-minute run would have stopped on drift without anyone being told.

Fixes made before the drill was run, and verified by it:

- **New `ingestion` readiness check** (`IngestionHealthCheck`): Degraded as soon as the newest finished run is `SchemaDrift`. Drift is the one outcome a retry can't fix, since every run checks the same contract and stops. A `Failed` run doesn't degrade it: the scheduler retries, and if failures persist, freshness catches them at 45 minutes. Runs still in progress are ignored, so a drift run starting doesn't briefly look like a recovery.
- **Freshness monitor** (`AlertPublisher`, an `IHealthCheckPublisher`): runs the readiness checks every minute (`Alerts:Period`) and alerts when the overall status changes, plus once at startup if it isn't Healthy. It logs every alert and POSTs it as JSON with a `text` field to `Alerts:WebhookUrl` when that is set. It runs in the API, not the worker, so it still fires when the worker has stopped.

Smaller findings:

- WireMock.Net logs a blank status code for callback responses without an explicit `WithStatusCode`; the drill tool now sets 200 so its console log reads cleanly.
- Alert text first used `Environment.NewLine`, which put `\r\n` into the webhook JSON on Windows; it now uses `\n`.

## Runbook: real schema drift

1. **Confirm it.** `GET /api/health/ready` shows `ingestion` Degraded with the drift details; `ops.ingest_run.error` has the same text. Fetch the live `layer?f=json` (or run `dotnet run --project src/Sac311.Worker -- verify-source`) to see the change yourself.
2. **Don't hurry.** Nothing is being written and the dashboard serves the last good figures. The watermark holds, so the first good run after the fix catches up on its own, however long the drift lasts. After 45 minutes `freshness` goes Degraded too and the dashboard badge shows Stale; that is expected.
3. **Decide what the change means.** A rename with the same values: update `SchemaContract.Fields` and `SourceRow.FromFeature` (which reads attributes by name). New values in a field: update the cleaner (and its seed tables) first, with a captured fixture (`worker capture-fixture`) and a unit test. A field dropped for good: decide what the metric does without it before touching code.
4. **Ship the fix** through CI, then run `dotnet run --project src/Sac311.Worker -- incremental` once by hand and check it Succeeded. Readiness turns Healthy within a minute and the recovery alert fires.
5. **If raw values changed meaning** for rows already loaded, step 3's cleaner or seed change only reaches new and edited rows. Run `dotnet run --project src/Sac311.Worker -- reclean` to apply it to every request the source still serves: it fetches the whole feed again (resumable if killed), rewrites only rows whose cleaned values differ (`rows_recleaned` in `ops.ingest_run`, no history rows) and refreshes the aggregates. Then check the DQ results (`worker dq`).
6. **Write it up** here, with the timeline and the before/after counts.
