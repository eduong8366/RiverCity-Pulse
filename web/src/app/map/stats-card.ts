import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { bulkLine } from '../lib/clear-outs';
import { formatCount, formatDays, formatTrend } from '../lib/format';
import { CardContent } from './card';
import { MIN_CLOSED } from './bins';

/**
 * The map's hover card: median and p90 days to close, closed, open now and the trend. When the figures include
 * clear-outs it says how many, with the notes it is given.
 */
@Component({
  selector: 'app-stats-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './stats-card.html',
  styleUrl: './stats-card.scss',
})
export class StatsCardComponent {
  readonly content = input.required<CardContent>();
  /** Clear-out notes to show (the latest few in the period). */
  readonly notes = input<readonly string[]>([]);
  /** Further clear-outs in the period not shown here. */
  readonly moreNotes = input(0);

  protected readonly days = formatDays;
  protected readonly count = formatCount;
  protected readonly minClosed = MIN_CLOSED;
  protected readonly trend = computed(() =>
    formatTrend(this.content().trend, this.content().windowDays),
  );
  protected readonly bulk = computed(() =>
    bulkLine(this.content().closed, this.content().bulkClosed),
  );

  /** Scrolls to the clear-out notes without touching the URL (a fragment would drop the filters from it). */
  protected jump(event: Event): void {
    event.preventDefault();
    document.getElementById('clear-outs')?.scrollIntoView({ behavior: 'smooth' });
  }
}
