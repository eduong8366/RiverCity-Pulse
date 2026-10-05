import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import type { ECharts } from 'echarts/core';
import { BacklogPoint, ClearOutNote } from '../api/models';
import { formatCount, formatDate } from '../lib/format';
import { backlogOption } from './backlog-option';

/** ECharts backlog chart, weekly or daily. ECharts is loaded on first render, outside the initial bundle. */
@Component({
  selector: 'app-backlog-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './backlog-chart.html',
  styleUrl: './backlog-chart.scss',
})
export class BacklogChartComponent {
  readonly points = input<readonly BacklogPoint[]>([]);
  /** Clear-outs to mark (already filtered to the selected category). */
  readonly clearOuts = input<readonly ClearOutNote[]>([]);
  /** The selected council district, if any: the notes are citywide, and the readout says so. */
  readonly district = input<number | null>(null);
  /** How the points are grouped: by week (Monday labels) or by day. */
  readonly grain = input<'day' | 'week'>('week');

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('chart');
  private readonly chart = signal<ECharts | null>(null);

  /** One sentence for screen readers: where the backlog stands now. */
  protected readonly summary = computed(() => {
    const points = this.points();
    const last = points.at(-1);
    const weekly = this.grain() === 'week';
    return last
      ? `${weekly ? 'Weekly' : 'Daily'} from ${formatDate(points[0].date)}. ` +
          `${weekly ? 'In the week of' : 'On'} ${formatDate(last.date)}, ` +
          `${formatCount(last.opened)} requests were opened, ${formatCount(last.closed)} closed, ` +
          `and ${formatCount(last.open)} were open at the end.`
      : '';
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(async () => {
      const { echarts } = await import('./echarts');
      if (destroyRef.destroyed) {
        return;
      }
      const element = this.host().nativeElement;
      const chart = echarts.init(element);
      const resize = new ResizeObserver(() => chart.resize());
      resize.observe(element);
      destroyRef.onDestroy(() => {
        resize.disconnect();
        chart.dispose();
      });
      this.chart.set(chart);
    });

    effect(() => {
      const chart = this.chart();
      const points = this.points();
      if (chart && points.length > 0) {
        chart.setOption(backlogOption(points, this.clearOuts(), this.district(), this.grain()), {
          notMerge: true,
        });
      }
    });
  }
}
