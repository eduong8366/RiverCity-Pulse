import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { CategorySummaryResponse, CellStats } from '../api/models';
import { FilterStore } from '../filters/filter-store';
import { formatCount, formatDays } from '../lib/format';

export type SortKey =
  'category' | 'opened' | 'closed' | 'median' | 'p90' | 'change' | 'open' | 'openAge' | 'bulk';

export interface Sort {
  key: SortKey;
  descending: boolean;
}

interface Row {
  category: string;
  stats: CellStats;
}

const values: Record<Exclude<SortKey, 'category'>, (s: CellStats) => number | null> = {
  opened: (s) => s.current.opened,
  closed: (s) => s.current.closed,
  median: (s) => s.current.medianDays,
  p90: (s) => s.current.p90Days,
  // A trend from a median of 0 has no percent; it sorts as the largest rise.
  change: (s) => (s.trend === null ? null : (s.trend.medianChangePct ?? Number.POSITIVE_INFINITY)),
  open: (s) => s.openBacklog,
  openAge: (s) => s.medianOpenAgeDays,
  bulk: (s) => s.current.bulkClosed,
};

/** Sorts the rows by one column; missing figures (no median, no trend) always go last. */
export function sortRows(rows: readonly Row[], sort: Sort): Row[] {
  const sign = sort.descending ? -1 : 1;
  return [...rows].sort((a, b) => {
    if (sort.key === 'category') {
      return sign * a.category.localeCompare(b.category);
    }
    const x = values[sort.key](a.stats);
    const y = values[sort.key](b.stats);
    if (x === null || y === null) {
      return x === y ? a.category.localeCompare(b.category) : x === null ? 1 : -1;
    }
    return sign * (x - y) || a.category.localeCompare(b.category);
  });
}

/**
 * Every category group's figures for the window and district (/api/categories/summary), with the total, sortable by
 * any column. A category's name selects it as the dashboard's category filter.
 */
@Component({
  selector: 'app-categories-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './categories-table.html',
  styleUrl: './categories-table.scss',
})
export class CategoriesTableComponent {
  protected readonly store = inject(FilterStore);

  readonly summary = input.required<CategorySummaryResponse>();

  protected readonly sort = signal<Sort>({ key: 'opened', descending: true });
  protected readonly rows = computed(() => sortRows(this.summary().categories, this.sort()));

  protected readonly columns: { key: SortKey; label: string; numeric: boolean }[] = [
    { key: 'category', label: 'Category', numeric: false },
    { key: 'opened', label: 'Opened', numeric: true },
    { key: 'closed', label: 'Closed', numeric: true },
    { key: 'median', label: 'Median days', numeric: true },
    { key: 'p90', label: '90th pct.', numeric: true },
    { key: 'change', label: 'Change', numeric: true },
    { key: 'open', label: 'Open now', numeric: true },
    { key: 'openAge', label: 'Median open age', numeric: true },
    { key: 'bulk', label: 'In clear-outs', numeric: true },
  ];

  protected readonly days = formatDays;
  protected readonly count = formatCount;
  protected readonly caption = computed(
    () => `Days to close by category, last ${this.summary().windowDays} days`,
  );

  /** Clicking the sorted column flips it; another column starts descending (largest first), names ascending. */
  protected sortBy(key: SortKey): void {
    const current = this.sort();
    this.sort.set(
      current.key === key
        ? { key, descending: !current.descending }
        : { key, descending: key !== 'category' },
    );
  }

  protected ariaSort(key: SortKey): string | null {
    const current = this.sort();
    return current.key === key ? (current.descending ? 'descending' : 'ascending') : null;
  }

  protected arrow(key: SortKey): string {
    const sort = this.ariaSort(key);
    return sort === 'ascending' ? '▲' : sort === 'descending' ? '▼' : '';
  }

  /** "+21.4%", "−3.0%", "steady", or "–" without a trend. */
  protected change(stats: CellStats): string {
    const trend = stats.trend;
    if (trend === null) {
      return '–';
    }
    if (trend.direction === 'steady') {
      return 'steady';
    }
    if (trend.medianChangePct === null) {
      return 'up from 0';
    }
    const pct = Math.abs(trend.medianChangePct).toLocaleString('en-US', {
      minimumFractionDigits: 1,
      maximumFractionDigits: 1,
    });
    return `${trend.medianChangePct > 0 ? '+' : '−'}${pct}%`;
  }

  protected pick(category: string): void {
    this.store.category.set(this.store.category() === category ? null : category);
  }
}
