import { httpResource } from '@angular/common/http';
import { computed, inject, Injectable, signal } from '@angular/core';
import { FilterStore } from '../filters/filter-store';
import { chartWindow } from '../filters/filters';
import {
  BacklogResponse,
  CategorySummaryResponse,
  ClearOutsResponse,
  ExclusionsResponse,
  FreshnessResponse,
  MapResponse,
  NeighborhoodBoundaries,
  SlowerBy,
  SlowerResponse,
} from './models';

/** Query parameters with the unset filters left out (the API reads a missing one as "all"). */
function params(values: Record<string, string | number | null>): Record<string, string | number> {
  return Object.fromEntries(Object.entries(values).filter(([, v]) => v !== null)) as Record<
    string,
    string | number
  >;
}

/** Every API response the dashboard shows, reloaded when the filters it depends on change. */
@Injectable({ providedIn: 'root' })
export class DashboardData {
  private readonly filters = inject(FilterStore);

  readonly freshness = httpResource<FreshnessResponse>(() => '/api/meta/freshness');

  readonly boundaries = httpResource<NeighborhoodBoundaries>(() => '/api/geo/neighborhoods');

  /** Category list for the filter: the service categories with requests in the last year, citywide. */
  readonly categoryList = httpResource<CategorySummaryResponse>(() => ({
    url: '/api/categories/summary',
    params: { window: 365 },
  }));

  readonly categories = computed(() =>
    (this.categoryList.hasValue() ? this.categoryList.value().categories : [])
      .map((c) => c.category)
      .sort((a, b) => a.localeCompare(b)),
  );

  readonly summary = httpResource<CategorySummaryResponse>(() => ({
    url: '/api/categories/summary',
    params: params({ window: this.filters.window(), district: this.filters.district() }),
  }));

  readonly map = httpResource<MapResponse>(() => ({
    url: '/api/map/neighborhoods',
    params: params({
      window: this.filters.window(),
      category: this.filters.category(),
      district: this.filters.district(),
    }),
  }));

  /** The backlog chart's range, counted back from the aggregates' as-of date (today until freshness loads). */
  readonly chart = computed(
    () => {
      const asOf =
        (this.freshness.hasValue() ? this.freshness.value().aggregatesAsOfDate : null) ??
        new Date().toISOString().slice(0, 10);
      return chartWindow(this.filters.range(), asOf);
    },
    // Equal by value, so freshness arriving doesn't refetch an unchanged range.
    { equal: (a, b) => a.from === b.from && a.grain === b.grain },
  );

  readonly backlog = httpResource<BacklogResponse>(() => ({
    url: '/api/backlog',
    params: params({
      category: this.filters.category(),
      district: this.filters.district(),
      from: this.chart().from,
      grain: this.chart().grain,
    }),
  }));

  /** What the slower panel ranks. With a category selected it can only be neighborhoods (within that category). */
  readonly slowerChoice = signal<SlowerBy>('neighborhood');
  readonly slowerBy = computed<SlowerBy>(() =>
    this.filters.category() ? 'neighborhood' : this.slowerChoice(),
  );

  readonly slower = httpResource<SlowerResponse>(() => ({
    url: '/api/trends/slower',
    params: params({
      by: this.slowerBy(),
      window: this.filters.window(),
      category: this.filters.category(),
      district: this.filters.district(),
    }),
  }));

  /** The selected neighborhood's figures by category (nothing requested while none is selected). */
  readonly neighborhood = httpResource<CategorySummaryResponse>(() => {
    const slug = this.filters.neighborhood();
    return slug
      ? {
          url: '/api/categories/summary',
          params: params({
            window: this.filters.window(),
            district: this.filters.district(),
            neighborhood: slug,
          }),
        }
      : undefined;
  });

  readonly exclusions = httpResource<ExclusionsResponse>(() => ({
    url: '/api/meta/exclusions',
    params: { window: this.filters.window() },
  }));

  /** Every clear-out since 2024 (a few dozen rows); filtered by category and date on the client. */
  readonly clearOuts = httpResource<ClearOutsResponse>(() => '/api/meta/clear-outs');
}
