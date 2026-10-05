import { CategorySummaryResponse, MapNeighborhood, Trend } from '../api/models';
import { Filters } from '../filters/filters';

/** What the hover card shows: one neighborhood, or the whole selection when nothing is hovered. */
export interface CardContent {
  title: string;
  scope: string;
  closed: number;
  bulkClosed: number;
  medianDays: number | null;
  p90Days: number | null;
  openBacklog: number;
  trend: Trend | null;
  windowDays: number;
}

/** "Parking · District 4 · last 90 days" */
export function scopeLabel(filters: Filters): string {
  return [
    filters.category ?? 'All categories',
    filters.district ? `District ${filters.district}` : 'Whole city',
    `last ${filters.window} days`,
  ].join(' · ');
}

/** A hovered neighborhood. `figures` is undefined when it has no requests under the current filters. */
export function neighborhoodCard(
  name: string,
  figures: MapNeighborhood | undefined,
  filters: Filters,
): CardContent {
  return {
    title: name,
    scope: scopeLabel(filters),
    closed: figures?.closed ?? 0,
    bulkClosed: figures?.bulkClosed ?? 0,
    medianDays: figures?.medianDays ?? null,
    p90Days: figures?.p90Days ?? null,
    openBacklog: figures?.openBacklog ?? 0,
    trend: figures?.trend ?? null,
    windowDays: filters.window,
  };
}

/** Nothing hovered: the citywide (or district) total, or the selected category's figures. */
export function summaryCard(summary: CategorySummaryResponse, filters: Filters): CardContent {
  const stats = filters.category
    ? summary.categories.find((c) => c.category.toLowerCase() === filters.category?.toLowerCase())
        ?.stats
    : summary.total;
  return {
    title: filters.district ? `District ${filters.district}` : 'Citywide',
    scope: scopeLabel(filters),
    closed: stats?.current.closed ?? 0,
    bulkClosed: stats?.current.bulkClosed ?? 0,
    medianDays: stats?.current.medianDays ?? null,
    p90Days: stats?.current.p90Days ?? null,
    openBacklog: stats?.openBacklog ?? 0,
    trend: stats?.trend ?? null,
    windowDays: filters.window,
  };
}
