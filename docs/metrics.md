# How RiverCity Pulse measures

This page defines the numbers the API and dashboard show, lists everything they leave out and why, and gives the SQL to recompute them yourself. Every exclusion is also published with its count at [`/api/meta/exclusions`](../README.md#api).

## Principles

- **Source records are never changed or deleted.** The cleaned table keeps every request as the city published it. Exclusions are flags and classifications computed next to the data, and they are recomputed on every refresh.
- **Headline numbers follow a published definition.** That definition is this page.
- **Anything left out stays visible**, as its own number with its reason, in `/api/meta/exclusions`.
- **Requests are classified by what they are, never by how fast they closed.** Dropping fast or slow closures because of their speed would be cherry-picking. Speed is only used to *find* candidates. The decision is made per request type, by hand, and recorded in a seed file with a reason.

## The headline numbers

| Figure | Definition |
|---|---|
| Median and p90 days to close | `PERCENTILE_CONT` over service requests closed in the period, with a trustworthy time to close (not a bulk closure and no date problem) |
| Closed | How many requests the median and p90 are over |
| Excluded | Closed service requests left out of timing: bulk closures plus date problems |
| Bulk closed | The part of Excluded that was closed in a clear-out |
| Opened | Service requests created in the period |
| Open now, median open age | Service requests open at the as-of time, and the median of their ages |
| Backlog series | Service requests opened, closed and open per day from 2024-01-01 |

A window of N days (30, 90 or 365) has a *current* period (the last N days through the as-of date, Sacramento time) and a *prior* period (the N days before it). The trend compares the two medians. It is null when either period has fewer than 30 closed requests, and "steady" within 5%.

## What is left out

### 1. Non-service requests (out of every figure)

Some records in the 311 feed are not requests for service: information calls answered on the phone, items waiting in an email inbox, referrals to someone else. They close in minutes or never, so counting them as service work pulls the median down (information calls) and inflates the open backlog (the inbox). They are left out of **every** headline figure, including opened, open now and the backlog series, and the categories list doesn't offer them as a filter.

A request is non-service when either of these holds:

- **Its whole category is non-service** (`ref.category_map.is_service = 0`, with a reason). These are the Other and Process/Unclassified groups:

  | CategoryLevel1 | Reason |
  |---|---|
  | Other | Information and referral calls (mostly "Information" and "Non City"), logged and closed during the call |
  | Review | An inbox bucket (mostly "Email Review"): items waiting to be read, not requests for service |
  | Escalation | A routing bucket (to a specialist or supervisor): says how a call was handled, not what service was asked for |
  | Web Form | A form bucket (mostly "Mayor Form"): correspondence, not a request for service |
  | Community Tag, Homeless Services, DCR, CDD-Planning | Too few requests to report on their own, so grouped as Other |

- **Its line inside a service category is non-service** (`ref.non_service_type`, a CategoryLevel1 + CategoryLevel2 pair with a reason):

  | Category / line | Reason |
  |---|---|
  | Animal Control / General - Other | Information call on another topic |
  | Animal Control / General - Duplicate Request | A duplicate of a request already logged; the original is counted |
  | Animal Control / General - After Hours Call | An after-hours call logged for the record |
  | Animal Control / General - Adoption, - Spay/ Neuter, - Investigation, - License, - Found Animal, - Barking | Information calls on those topics (the work itself has its own lines, e.g. "Found Animal", "Complaint - Barking") |
  | Building and Planning / General | Information call |
  | Parking / General | Information call |
  | Parks / General | Information call |
  | Urban Forestry / General | Information call |
  | Shared Rideables / Lime, Shared Rideables / Bird | Referred to the operator; the city does not do the work |

Categories that aren't in the map ("Unmapped") count as service, so nothing disappears silently. Those requests already carry the `UnmappedCategory` data-quality flag.

**How the lines were chosen (2026-10-04).** The candidate query below lists every category line with 100+ requests since 2024 where 90% or more of the closures happened within an hour. Each candidate was then judged by its name. Lines that are information, duplicates, after-hours logging or referrals were added. Lines that name real work stay counted, however fast they close:

| Candidate (closed within an hour) | Decision |
|---|---|
| Code Enforcement / Business Compliance Taxi Cab (96%) | Counted: a compliance case |
| Homeless Camp / Homeless Camp-Trash SPD (99%) | Counted: a cleanup |
| Parks / Request Ranger (90%) | Counted: a ranger dispatch |
| Streets / Alley Gate (96%) | Counted: names a work item |

Lines under 90% are not candidates, so they stay counted even when the name says "General": Animal Control / General (89%), Streets / General (88%), Facilities / General (81%), Code Enforcement / General (74% in the last 90 days) and Homeless Camp / General (72%). Solid Waste, Water and Utility Billing "General" are real work (0–0.2% within an hour).

To change the list, edit `db/seed/category_map.sql` or `db/seed/non_service_type.sql` and run `worker migrate`. The next refresh reclassifies every request, with no reclean needed.

### 2. Bulk closures (out of the median and p90 only)

Sometimes a department closes a large batch of old requests on one day. These are clear-outs of stale records, not work finished that day. On 2026-08-10, for example, 10,820 Parking requests averaging 564 days old were closed at once. Timed as normal closures, they dragged neighborhood medians to hundreds of days (Pell/Main Industrial Park showed 664).

**Rule** (`Sac311.Domain.BulkClosureRule`, applied by `usp_classify_for_metrics`):

- A (closed date, category group) is a **clear-out** when at least **100** of its closures are older than **180 days**. Only service requests with no date problem count toward the 100.
- In a clear-out, every closure older than **90 days** gets the `BulkClosure` flag. A clear-out sweeps up younger requests in the same minute too (576 Parking requests aged 90–180 days were closed with the August 10 batch), but same-day closures under 90 days stay counted.
- Two categories never combine, and the rule has no configuration setting: the values live in code and on this page.

Bulk closures are left out of the median and p90 only. They still count as *closed*, and they still leave the backlog, because the clear-out really did empty it. `excluded` includes them, and `bulkClosed` counts them separately.

**Accepted limit:** a batch of fewer than 100 old closures stays counted. On 2026-10-04 there were such batches just under the line (Parking 99 on 2026-09-14, 90 on 2026-08-26 and 2026-09-24; Sewer 91 on 2026-08-21). They are behind Old Sacramento's 90-day p90 of 608 days. The threshold was set before looking at these, and it is not moved to catch them.

The data-quality checks count `BulkClosure` flags after every run, so a new clear-out raises a warning.

### 3. Date problems (out of the median and p90 only)

Closed requests whose time to close can't be trusted:

| Flag | Meaning |
|---|---|
| `SentinelDate` | A date before 2000 (the 1899-12-30 field default) was set to NULL |
| `FutureDate` | A date more than one day after the time it was cleaned |
| `InvalidCloseOrder` | DateClosed is earlier than DateCreated |
| `ClosedMissingDate` | The status is CLOSED but DateClosed is empty |

`/api/meta/exclusions` lists each one with its count. A request can have two of these flags.

## Effect (90-day window to 2026-10-04, citywide)

| | Closed requests timed | Median days | p90 days |
|---|---|---|---|
| Everything except date problems | 157,135 | 2.02 | 178.7 |
| Without bulk closures only | 142,771 | 1.16 | 31.6 |
| Without non-service only | 122,273 | 5.89 | 328.0 |
| **Headline (both left out)** | **107,909** | **3.88** | **37.6** |

The two distortions pull in opposite directions. Bulk closures inflate the median, while information calls deflate it, so the 2.02 days before exclusions was close to a believable number only by accident.

- **Open now:** 87,789 → **45,795** (41,994 non-service, 41,166 of them inbox items). Median open age 275 → **104** days.
- **Neighborhoods** with 30+ closed: medians range from 0.93 to 14.24 days (Village 12 highest), where Pell/Main showed 664 before.
- **Trend:** the citywide median is **20% faster** than the prior 90 days (3.88 vs 4.85). On 2026-10-02, before the exclusions, the same headline read 25.3% slower.

## Check it yourself

Recompute the 90-day citywide median and p90 from the cleaned table and the seeds alone, without the stored flags. This gives the same numbers as `/api/categories/summary?window=90` for the same as-of date. `dq_flags & 15` is the four date-problem flags, and `dbo.fn_map_key` is the SQL copy of the key function the seeds use. The CTE is called `purge`, not `bulk`, because BULK is a reserved word.

```sql
DECLARE @as_of date = '2026-10-04', @window int = 90;
DECLARE @min_count int = 100, @detect_age_days int = 180, @member_age_days int = 90;
WITH typed AS
(
    SELECT r.*, dbo.fn_map_key(r.category_level1) AS level1_key, dbo.fn_map_key(r.category_level2) AS level2_key
    FROM dbo.service_request AS r
    WHERE r.source_removed_utc IS NULL
),
svc AS
(
    SELECT t.* FROM typed AS t
    WHERE NOT EXISTS (SELECT 1 FROM ref.category_map AS cm WHERE cm.category_key = t.level1_key AND cm.is_service = 0)
      AND NOT EXISTS (SELECT 1 FROM ref.non_service_type AS n WHERE n.category_key = t.level1_key AND n.category_level2_key = t.level2_key)
),
purge AS
(
    SELECT closed_date_local, category_group FROM svc
    WHERE dq_flags & 15 = 0 AND days_to_close > @detect_age_days
    GROUP BY closed_date_local, category_group
    HAVING COUNT(*) >= @min_count
)
SELECT DISTINCT
    COUNT(*) OVER () AS closed,
    PERCENTILE_CONT(.5) WITHIN GROUP (ORDER BY s.days_to_close) OVER () AS median_days,
    PERCENTILE_CONT(.9) WITHIN GROUP (ORDER BY s.days_to_close) OVER () AS p90_days
FROM svc AS s
LEFT JOIN purge AS p ON p.closed_date_local = s.closed_date_local AND p.category_group = s.category_group
WHERE s.dq_flags & 15 = 0 AND s.days_to_close IS NOT NULL
  AND s.closed_date_local > DATEADD(day, -@window, @as_of) AND s.closed_date_local <= @as_of
  AND NOT (p.closed_date_local IS NOT NULL AND s.days_to_close > @member_age_days);
```

On 2026-10-04 this returned 107,909 closed, median 3.88 and p90 37.60, the same as the API.

The candidate query used to find non-service lines (it finds candidates only, and each one is then judged by its name):

```sql
SELECT category_group, category_level1, category_level2, COUNT(*) AS requests,
       CAST(100.0 * SUM(CASE WHEN days_to_close <= 0.04 THEN 1 ELSE 0 END)
            / NULLIF(SUM(CASE WHEN days_to_close IS NOT NULL THEN 1 ELSE 0 END), 0) AS decimal(5, 1)) AS pct_closed_within_hour
FROM dbo.service_request
WHERE created_date_local >= '2024-01-01' AND category_group NOT IN ('Other', 'Process/Unclassified')
GROUP BY category_group, category_level1, category_level2
HAVING COUNT(*) >= 100
   AND 100.0 * SUM(CASE WHEN days_to_close <= 0.04 THEN 1 ELSE 0 END)
       / NULLIF(SUM(CASE WHEN days_to_close IS NOT NULL THEN 1 ELSE 0 END), 0) >= 90
ORDER BY category_group, requests DESC;
```

`days_to_close` is stored to two decimals, so `<= 0.04` means within an hour.

## Checked and left alone

- **Solid Waste's nightly batch closes:** about 290 closures at around 6:01 AM Pacific, averaging about 30 days old. That is real bulky-pickup work recorded by a nightly sync.
- **Utility Billing / General:** median 1.00 and p90 1.01 days. It looks like a fixed one-day process.
- **Old open requests** in Streets (back to 2022) and Facilities (765 over 2 years) are a real backlog.
- **Long p90 tails** that remain (Parking, Sewer, Drains) are real tails. The p90 is there to show them.
- **A repeat address** (one address with 100+ new requests in 7 days, e.g. 6005 Wardell Way's web "Owned Animal Complaint" requests) is a data-quality warning, not an exclusion.
