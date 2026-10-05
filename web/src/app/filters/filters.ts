import { weekStart } from '../lib/clear-outs';

/**
 * The dashboard's filters, as the API takes them. `null` means all categories, the whole city, or no neighborhood
 * selected. `neighborhood` opens the detail drawer; `range` is how much of the backlog chart to show.
 */
export interface Filters {
  window: WindowDays;
  category: string | null;
  district: number | null;
  neighborhood: string | null;
  range: ChartRange;
}

export const WINDOWS = [30, 90, 365] as const;
export type WindowDays = (typeof WINDOWS)[number];

export const DISTRICTS = [1, 2, 3, 4, 5, 6, 7, 8] as const;

/** Backlog chart ranges: everything since 2024 by week, the last 12 months by week, the last 90 days by day. */
export const CHART_RANGES = [
  { value: 'all', label: 'Since 2024, weekly' },
  { value: '1y', label: 'Last 12 months, weekly' },
  { value: '90d', label: 'Last 90 days, daily' },
] as const;
export type ChartRange = (typeof CHART_RANGES)[number]['value'];

export const DEFAULT_FILTERS: Filters = {
  window: 90,
  category: null,
  district: null,
  neighborhood: null,
  range: 'all',
};

/** A neighborhood slug as the API writes them ("central-oak-park"). */
const SLUG = /^[a-z0-9]+(-[a-z0-9]+)*$/;

/**
 * Reads filters from a query string (`?window=30&category=Parking&district=4&neighborhood=downtown&range=1y`); bad
 * values fall back to the defaults.
 */
export function parseFilters(query: string): Filters {
  const params = new URLSearchParams(query);
  const window = Number(params.get('window'));
  const district = Number(params.get('district'));
  const category = params.get('category')?.trim();
  const neighborhood = params.get('neighborhood')?.trim().toLowerCase() ?? '';
  const range = params.get('range');
  return {
    window: (WINDOWS as readonly number[]).includes(window)
      ? (window as WindowDays)
      : DEFAULT_FILTERS.window,
    category: category ? category : null,
    district: (DISTRICTS as readonly number[]).includes(district) ? district : null,
    neighborhood: SLUG.test(neighborhood) ? neighborhood : null,
    range: CHART_RANGES.some((r) => r.value === range)
      ? (range as ChartRange)
      : DEFAULT_FILTERS.range,
  };
}

/** The query string for `filters`, leaving out defaults (so the default view has a clean URL). */
export function filtersToQuery(filters: Filters): string {
  const params = new URLSearchParams();
  if (filters.window !== DEFAULT_FILTERS.window) {
    params.set('window', String(filters.window));
  }
  if (filters.category) {
    params.set('category', filters.category);
  }
  if (filters.district) {
    params.set('district', String(filters.district));
  }
  if (filters.neighborhood) {
    params.set('neighborhood', filters.neighborhood);
  }
  if (filters.range !== DEFAULT_FILTERS.range) {
    params.set('range', filters.range);
  }
  return params.toString();
}

/**
 * The backlog request for a chart range ending on `asOf` (an ISO date): the first day to ask for (null: from the
 * start, 2024-01-01) and the grain.
 */
export function chartWindow(
  range: ChartRange,
  asOf: string,
): { from: string | null; grain: 'day' | 'week' } {
  if (range === 'all') {
    return { from: null, grain: 'week' };
  }
  const day = new Date(`${asOf}T00:00:00Z`);
  if (range === '1y') {
    day.setUTCFullYear(day.getUTCFullYear() - 1);
    // From a Monday, so the first week is a whole one.
    return { from: weekStart(day.toISOString().slice(0, 10)), grain: 'week' };
  }
  day.setUTCDate(day.getUTCDate() - 89);
  return { from: day.toISOString().slice(0, 10), grain: 'day' };
}
