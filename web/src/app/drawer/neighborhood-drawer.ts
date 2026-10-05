import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  viewChild,
} from '@angular/core';
import { CategorySummaryResponse } from '../api/models';
import { FilterStore } from '../filters/filter-store';
import { bulkLine } from '../lib/clear-outs';
import { formatCount, formatDate, formatDays, formatTrend } from '../lib/format';

/**
 * The selected neighborhood in detail: its figures against the prior period, what's open now, and the same by
 * category (from /api/categories/summary?neighborhood=). Opened by picking a neighborhood on the map or in the filter
 * bar; closing it (or Escape) clears the selection.
 */
@Component({
  selector: 'app-neighborhood-drawer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './neighborhood-drawer.html',
  styleUrl: './neighborhood-drawer.scss',
  host: { '(document:keydown.escape)': 'close()' },
})
export class NeighborhoodDrawerComponent {
  protected readonly store = inject(FilterStore);

  /** The neighborhood's name while its figures load (from the boundary file). */
  readonly name = input.required<string>();
  readonly summary = input<CategorySummaryResponse | undefined>();
  readonly loading = input(false);
  readonly failed = input(false);

  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');

  protected readonly days = formatDays;
  protected readonly count = formatCount;
  protected readonly date = formatDate;

  protected readonly title = computed(() => this.summary()?.neighborhood?.name ?? this.name());
  protected readonly total = computed(() => this.summary()?.total);
  protected readonly windowDays = computed(() => this.summary()?.windowDays ?? this.store.window());
  protected readonly scope = computed(() => {
    const district = this.store.district();
    return [
      district ? `District ${district}` : 'Whole city',
      `last ${this.windowDays()} days`,
      'all categories',
    ].join(' · ');
  });
  protected readonly compareCaption = computed(
    () =>
      `Days to close, last ${this.windowDays()} days against the ${this.windowDays()} days before`,
  );
  protected readonly categoryCaption = computed(
    () => `${this.title()} by category, last ${this.windowDays()} days`,
  );
  protected readonly trend = computed(() => {
    const total = this.total();
    return total ? formatTrend(total.trend, this.windowDays()) : '';
  });
  protected readonly bulk = computed(() => {
    const current = this.total()?.current;
    return current ? bulkLine(current.closed, current.bulkClosed) : null;
  });
  /** Categories with the most closed first. */
  protected readonly categories = computed(() =>
    [...(this.summary()?.categories ?? [])].sort(
      (a, b) =>
        b.stats.current.closed - a.stats.current.closed || a.category.localeCompare(b.category),
    ),
  );

  constructor() {
    // Move focus into the drawer when it opens or switches neighborhood, so keyboard users land on it.
    afterRenderEffect(() => {
      this.name();
      this.heading().nativeElement.focus();
    });
  }

  protected close(): void {
    this.store.neighborhood.set(null);
  }

  protected pickCategory(category: string): void {
    this.store.category.set(category);
  }
}
