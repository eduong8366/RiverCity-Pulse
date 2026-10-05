# Dashboard layout decision

**Decision (2026-10-02): layout A, map-dominant with the backlog chart below.**

## Options
Three prototypes were built in Claude Design ([canvas](https://claude.ai/artifact/TTBCJmPuawsLrqX8FrkGxm), private) from real API responses saved in [`sample/`](sample/) (90-day window, data as of 2026-10-02; re-exported 2026-10-04 with the metric exclusions, so the files now show the post-exclusion figures):

| | Layout | Notes |
|---|---|---|
| **A** | Map-dominant: large neighborhood choropleth with a "slowest neighborhoods" list beside it, backlog chart full width below | **Chosen** |
| B | 50/50 split: KPI strip on top, map and chart side by side | The map shrinks to half width, where small neighborhoods are hard to hover |
| C | Chart-first: backlog chart with headline numbers, map inset and category list on the right | The inset map is too small to read 129 neighborhoods |

## Why A
The product's main question is *where* requests are slow, and the neighborhood names match the boundary file exactly (129/129, see [source-profile.md](../source-profile.md)), so the map can carry the page. The backlog chart answers the second question (is it getting better?) and works at full width.

## What M4b builds from it
- **Page:** header with the freshness badge, filter bar (window 30/90/365, category, council district), map card, chart card, source and non-affiliation footer.
- **`NeighborhoodMapComponent`:** Leaflet choropleth of `medianDays` from `/api/map/neighborhoods`. Five quantile bins over neighborhoods with at least 30 closed requests; neighborhoods under 30 closed are grey ("Under 30 closed"), matching the API's trend rule. With the metric exclusions ([`metrics.md`](../metrics.md)), the 90-day cuts on 2026-10-04 are 2.80 / 3.79 / 4.97 / 6.33 days over 122 neighborhoods (medians 0.93 to 14.24; 7 under 30 closed). The prototypes used 2.4 / 3.7 / 5.2 / 7.1 with a 664-day outlier, which was bulk closures; bins stay quantile-based and are computed from the response, not hard-coded. A hover card shows the neighborhood's median, p90, closed, open and trend; with nothing hovered it shows the citywide (or category) figures from `/api/categories/summary`.
- **`BacklogChartComponent`:** ECharts, weekly from 2024-01-01: open backlog as an area, opened vs closed per week as a smaller panel below, a hover readout, and a footnote that the early 2024 backlog may be undercounted (the feed drops requests last updated before 2024).
- **`FreshnessBadgeComponent`:** "Fresh / Stale", last successful run in Pacific time and the request count, from `/api/meta/freshness`.
- **"How we measure" panel:** every exclusion from `/api/meta/exclusions` with its count and reason, linked to [`metrics.md`](../metrics.md); no "as recorded" toggle (decided 2026-10-04). Sample: [`sample/exclusions-90.json`](sample/exclusions-90.json).
- **Deferred to M6:** the "slowest neighborhoods" list (it belongs with the slower panel and the neighborhood drawer); B's KPI strip and C's category list are candidates for the categories table.

## Visual system
- Type: Public Sans (Google Fonts), tabular numerals for figures.
- Colors: ink `#10242B`, muted text `#4E6168`, page `#F2F5F4`, cards `#FFFFFF` with `#D9E1E0` borders, accent teal `#0B6E69`.
- Trend: slower `#B4480E` (orange), faster `#1F5FA6` (blue), steady muted. Blue/orange rather than red/green.
- Choropleth ramp (fast → slow): `#E3F1EE`, `#AFD8D0`, `#6DB6AC`, `#2E8B83`, `#0B5A56`; no data `#DCE1E1`.
- Controls are at least 44 px tall; the window picker is a segmented button group with `aria-pressed`.
