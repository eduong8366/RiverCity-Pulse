import { Location } from '@angular/common';
import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { Filters, filtersToQuery, parseFilters, WindowDays } from './filters';

/** The current filters as signals, read from the URL at start and written back to it on every change. */
@Injectable({ providedIn: 'root' })
export class FilterStore {
  private readonly location = inject(Location);
  private readonly initial = parseFilters(this.query());

  readonly window = signal<WindowDays>(this.initial.window);
  readonly category = signal<string | null>(this.initial.category);
  readonly district = signal<number | null>(this.initial.district);

  readonly filters = computed<Filters>(() => ({
    window: this.window(),
    category: this.category(),
    district: this.district(),
  }));

  constructor() {
    // replaceState, not a new history entry per click: Back leaves the dashboard rather than undoing a filter.
    effect(() => {
      const path = this.location.path().split('?')[0];
      this.location.replaceState(path, filtersToQuery(this.filters()));
    });
  }

  private query(): string {
    const path = this.location.path();
    const start = path.indexOf('?');
    return start < 0 ? '' : path.slice(start);
  }
}
