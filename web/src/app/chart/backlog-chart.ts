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
import { BacklogPoint } from '../api/models';
import { formatCount, formatDate } from '../lib/format';
import { backlogOption } from './backlog-option';

/** ECharts backlog chart, weekly from 2024-01-01. ECharts is loaded on first render, outside the initial bundle. */
@Component({
  selector: 'app-backlog-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './backlog-chart.html',
  styleUrl: './backlog-chart.scss',
})
export class BacklogChartComponent {
  readonly points = input<readonly BacklogPoint[]>([]);

  private readonly host = viewChild.required<ElementRef<HTMLElement>>('chart');
  private readonly chart = signal<ECharts | null>(null);

  /** One sentence for screen readers: where the backlog stands now. */
  protected readonly summary = computed(() => {
    const points = this.points();
    const last = points.at(-1);
    return last
      ? `Weekly from ${formatDate(points[0].date)}. In the week of ${formatDate(last.date)}, ` +
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
        chart.setOption(backlogOption(points), { notMerge: true });
      }
    });
  }
}
