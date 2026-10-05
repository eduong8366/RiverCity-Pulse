import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { SlowerBy, SlowerResponse } from '../api/models';
import { FilterStore } from '../filters/filter-store';
import { formatCount, formatDays } from '../lib/format';

/** "76 of 129 neighborhoods got slower against the prior 90 days: …" */
export function comparedSentence(response: SlowerResponse): string {
  const { slower, steady, faster, noTrend } = response.compared;
  const total = slower + steady + faster + noTrend;
  const noun = response.by === 'neighborhood' ? 'neighborhoods' : 'categories';
  const parts = [`${faster} faster`, `${steady} steady`];
  const tail =
    noTrend > 0 ? `; ${noTrend} had under 30 closed in a period, too few to compare` : '';
  return (
    `${slower} of ${total} ${noun} got slower against the prior ${response.windowDays} days, ` +
    `${parts.join(', ')}${tail}.`
  );
}

/** "+12.3 days" */
export function formatAdded(days: number): string {
  return `+${formatDays(days)}`;
}

/**
 * What's getting slower (/api/trends/slower): the neighborhoods or categories whose median days to close rose most
 * against the prior period. A name opens that neighborhood's drawer, or selects that category. Clear-outs are counted
 * as recorded and raise a median, so each row says how many of its closures were in one.
 */
@Component({
  selector: 'app-slower-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './slower-panel.html',
  styleUrl: './slower-panel.scss',
})
export class SlowerPanelComponent {
  private readonly store = inject(FilterStore);

  readonly response = input<SlowerResponse | undefined>();
  readonly loading = input(false);
  readonly failed = input(false);
  /** What is ranked. */
  readonly by = input<SlowerBy>('neighborhood');
  /** True while a category is selected: only neighborhoods (within it) can be ranked. */
  readonly byLocked = input(false);
  readonly byChange = output<SlowerBy>();

  protected readonly days = formatDays;
  protected readonly count = formatCount;
  protected readonly added = formatAdded;

  protected readonly sentence = computed(() => {
    const response = this.response();
    return response ? comparedSentence(response) : '';
  });

  protected readonly caption = computed(() => {
    const r = this.response();
    const what = r?.by === 'category' ? 'Categories' : 'Neighborhoods';
    return r
      ? `${what} ranked by days added to the median, last ${r.windowDays} days against the ${r.windowDays} before`
      : '';
  });

  protected readonly options: { value: SlowerBy; label: string }[] = [
    { value: 'neighborhood', label: 'Neighborhoods' },
    { value: 'category', label: 'Categories' },
  ];

  protected open(key: string): void {
    if (this.response()?.by === 'category') {
      this.store.category.set(key);
    } else {
      this.store.neighborhood.set(key);
    }
  }
}
