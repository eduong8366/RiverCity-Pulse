/** The dashboard's filters, as the API takes them. `null` means all categories, or the whole city. */
export interface Filters {
  window: WindowDays;
  category: string | null;
  district: number | null;
}

export const WINDOWS = [30, 90, 365] as const;
export type WindowDays = (typeof WINDOWS)[number];

export const DISTRICTS = [1, 2, 3, 4, 5, 6, 7, 8] as const;

export const DEFAULT_FILTERS: Filters = { window: 90, category: null, district: null };

/** Reads filters from a query string (`?window=30&category=Parking&district=4`); bad values fall back to the defaults. */
export function parseFilters(query: string): Filters {
  const params = new URLSearchParams(query);
  const window = Number(params.get('window'));
  const district = Number(params.get('district'));
  const category = params.get('category')?.trim();
  return {
    window: (WINDOWS as readonly number[]).includes(window)
      ? (window as WindowDays)
      : DEFAULT_FILTERS.window,
    category: category ? category : null,
    district: (DISTRICTS as readonly number[]).includes(district) ? district : null,
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
  return params.toString();
}
