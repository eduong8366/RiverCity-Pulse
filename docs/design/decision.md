# Dashboard layout decision

**Decision (2026-10-02): layout A, map-dominant with the backlog chart below.**

## Options
Three prototypes were built in Claude Design ([canvas](https://claude.ai/artifact/TTBCJmPuawsLrqX8FrkGxm), private) from real API responses saved in [`sample/`](sample/) (90-day window, data as of 2026-10-02; re-exported on 2026-10-04 after the Session 7c method change, so the files show the current figures: non-service left out, clear-outs counted, plus [`clear-outs.json`](sample/clear-outs.json)):

| | Layout | Notes |
|---|---|---|
| **A** | Map-dominant: large neighborhood choropleth with a "slowest neighborhoods" list beside it, backlog chart full width below | **Chosen** |
| B | 50/50 split: KPI strip on top, map and chart side by side | The map shrinks to half width, where small neighborhoods are hard to hover |
| C | Chart-first: backlog chart with headline numbers, map inset and category list on the right | The inset map is too small to read 129 neighborhoods |

## Why A
The product's main question is *where* requests are slow, and the neighborhood names match the boundary file exactly (129/129, see [source-profile.md](../source-profile.md)), so the map can carry the page. The backlog chart answers the second question (is it getting better?) and works at full width.

## What M4b builds from it
- **Page:** header with the freshness badge, filter bar (window 30/90/365, category, council district), map card, chart card, source and non-affiliation footer.
- **`NeighborhoodMapComponent`:** Leaflet choropleth of `medianDays` from `/api/map/neighborhoods`. Five quantile bins over neighborhoods with at least 30 closed requests; neighborhoods under 30 closed are grey ("Under 30 closed"), matching the API's trend rule. With the current definitions ([`metrics.md`](../metrics.md): non-service left out, clear-outs counted as recorded), the 90-day cuts on 2026-10-04 are 3.83 / 4.97 / 7.12 / 11.76 days over 122 neighborhoods (medians 1.01 to 704.68; 7 under 30 closed). The top bin holds the neighborhoods hit by Parking clear-outs (Pell/Main Industrial Park 704.68, Old Sacramento 473.89), which is why the bins are quantiles: linear bins would put nearly everything in the first one. The prototypes used 2.4 / 3.7 / 5.2 / 7.1, and Session 7b's since-reverted bulk exclusion gave 2.80 / 3.79 / 4.97 / 6.33. Bins are computed from the response, not hard-coded. A neighborhood whose figures include a clear-out gets its note in the hover card. A hover card shows the neighborhood's median, p90, closed, open and trend; with nothing hovered it shows the citywide (or category) figures from `/api/categories/summary`.
- **`BacklogChartComponent`:** ECharts, weekly from 2024-01-01: open backlog as an area, opened vs closed per week as a smaller panel below, a marker on each clear-out date from `/api/meta/clear-outs` with its note in the hover readout, and a footnote that the early 2024 backlog may be undercounted (the feed drops requests last updated before 2024).
- **`FreshnessBadgeComponent`:** "Fresh / Stale", last successful run in Pacific time and the request count, from `/api/meta/freshness`.
- **"How we measure" panel:** every exclusion from `/api/meta/exclusions` with its count and reason, and every clear-out note from `/api/meta/clear-outs`, linked to [`metrics.md`](../metrics.md); no "as recorded" toggle (decided 2026-10-04). Notes use the API's factual sentence as is. Samples: [`sample/exclusions-90.json`](sample/exclusions-90.json), [`sample/clear-outs.json`](sample/clear-outs.json).
- **Deferred to M6:** the "slowest neighborhoods" list (it belongs with the slower panel and the neighborhood drawer); B's KPI strip and C's category list are candidates for the categories table.

## Visual system
- Type: Public Sans (Google Fonts), tabular numerals for figures.
- Colors: ink `#10242B`, muted text `#4E6168`, page `#F2F5F4`, cards `#FFFFFF` with `#D9E1E0` borders, accent teal `#0B6E69`.
- Trend: slower `#B4480E` (orange), faster `#1F5FA6` (blue), steady muted. Blue/orange rather than red/green.
- Choropleth ramp (fast → slow): `#E3F1EE`, `#AFD8D0`, `#6DB6AC`, `#2E8B83`, `#0B5A56`; no data `#DCE1E1`.
- Controls are at least 44 px tall; the window picker is a segmented button group with `aria-pressed`.
