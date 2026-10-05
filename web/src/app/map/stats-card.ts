import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { formatCount, formatDays, formatTrend } from '../lib/format';
import { CardContent } from './card';
import { MIN_CLOSED } from './bins';

/** The map's hover card: median and p90 days to close, closed, open now and the trend. */
@Component({
  selector: 'app-stats-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './stats-card.html',
  styleUrl: './stats-card.scss',
})
export class StatsCardComponent {
  readonly content = input.required<CardContent>();

  protected readonly days = formatDays;
  protected readonly count = formatCount;
  protected readonly minClosed = MIN_CLOSED;
  protected readonly trend = computed(() =>
    formatTrend(this.content().trend, this.content().windowDays),
  );
}
