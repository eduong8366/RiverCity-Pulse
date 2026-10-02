# RiverCity Pulse

[![CI](https://github.com/eduong8366/RiverCity-Pulse/actions/workflows/ci.yml/badge.svg)](https://github.com/eduong8366/RiverCity-Pulse/actions/workflows/ci.yml)

A data pipeline and dashboard for the City of Sacramento's public 311 service requests. A .NET worker ingests the city's ArcGIS feed into SQL Server (raw, staging, cleaned and history layers), an ASP.NET Core API serves aggregates, and an Angular dashboard shows how long requests take to close by neighborhood and category.

> Work in progress. Run instructions, the architecture diagram, API examples and screenshots will be added as the milestones land.

## Repository layout

| Path | Contents |
|---|---|
| `src/` | `Sac311.Domain`, `Sac311.Data`, `Sac311.Ingestion`, `Sac311.Worker`, `Sac311.Api` |
| `tests/` | Domain, integration and API test projects |
| `scripts/` | `create-db.ps1` (local database), `verify-source.ps1` (profiles the live feed) |
| `docs/` | [`source-profile.md`](docs/source-profile.md): measured facts about the source data and its terms of use |
| `data/geo/` | Sacramento neighborhood boundaries (GeoJSON, WGS84) |

## Local prerequisites

- .NET SDK 10.0.401 (pinned in `global.json`)
- SQL Server Express at `localhost\SQLEXPRESS` with Windows authentication. Run `./scripts/create-db.ps1` once. To use a different server, set `ConnectionStrings__Sac311`.

## Data source and terms

The data comes from the City of Sacramento's [311 Calls](https://www.arcgis.com/home/item.html?id=5b9a9448663f41b1898643b6d91201c4) open data, used under the city's [Open Data Terms of Use](https://www.cityofsacramento.gov/content/dam/portal/it/gis/open-data/OpenDataTermsOfUse.pdf). The city provides it as is and it may be incomplete. Metrics shown here are derived by this project and are not official city figures. This project is not affiliated with or endorsed by the City of Sacramento.
