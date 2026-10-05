import type { BarSeriesOption, LineSeriesOption, ScatterSeriesOption } from 'echarts/charts';
import type {
  AxisPointerComponentOption,
  GridComponentOption,
  LegendComponentOption,
  TooltipComponentOption,
} from 'echarts/components';
import type { ComposeOption } from 'echarts/core';
import { BacklogPoint, ClearOutNote } from '../api/models';
import { byDay, byWeek } from '../lib/clear-outs';
import { formatCount, formatDate } from '../lib/format';

export type BacklogOption = ComposeOption<
  | BarSeriesOption
  | LineSeriesOption
  | ScatterSeriesOption
  | AxisPointerComponentOption
  | GridComponentOption
  | LegendComponentOption
  | TooltipComponentOption
>;

const INK = '#10242B';
const MUTED = '#4E6168';
const BORDER = '#D9E1E0';
const ACCENT = '#0B6E69';
const OPENED = '#A9BCC0';
const CLOSED = '#2E8B83';

export function escapeHtml(text: string): string {
  return text.replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] as string,
  );
}

/**
 * The hover readout for one week (or day), with the notes of any clear-outs in it. The last one is usually still
 * running, so it says so. The notes describe citywide clear-outs, so a district view says that.
 */
export function weekReadout(
  point: BacklogPoint,
  isLast: boolean,
  notes: readonly ClearOutNote[] = [],
  district: number | null = null,
  grain: 'day' | 'week' = 'week',
): string {
  const rows = [
    [`Open at end of ${grain}`, point.open],
    ['Opened', point.opened],
    ['Closed', point.closed],
  ]
    .map(
      ([label, value]) =>
        `<tr><td>${label}</td><td class="num">${formatCount(value as number)}</td></tr>`,
    )
    .join('');
  const title = `${grain === 'week' ? 'Week of ' : ''}${formatDate(point.date)}${isLast ? ' (so far)' : ''}`;
  const readout = `<strong>${escapeHtml(title)}</strong><table>${rows}</table>`;
  if (notes.length === 0) {
    return readout;
  }
  const when = grain === 'week' ? 'this week' : 'on this day';
  const heading = district === null ? `Clear-outs ${when}` : `Clear-outs ${when} (citywide)`;
  const items = notes.map((n) => `<li>${escapeHtml(n.note)}</li>`).join('');
  return `${readout}<div class="clear-outs"><strong>${heading}</strong><ul>${items}</ul>Counted as recorded.</div>`;
}

/**
 * Backlog by week (or day): the open count as an area on top, opened vs closed per period as bars below, sharing one
 * time axis and one hover readout. Periods with a clear-out get a marker on the open line and its note in the readout.
 */
export function backlogOption(
  points: readonly BacklogPoint[],
  clearOuts: readonly ClearOutNote[] = [],
  district: number | null = null,
  grain: 'day' | 'week' = 'week',
): BacklogOption {
  const dates = points.map((p) => p.date);
  const periods = grain === 'week' ? byWeek(clearOuts) : byDay(clearOuts);
  const markers = points.filter((p) => periods.has(p.date)).map((p) => [p.date, p.open]);
  const axisLabel = {
    color: MUTED,
    // Month and year ("Aug 2026"); ECharts thins the labels to fit.
    formatter: (value: string) => formatDate(value).replace(/ \d+,/, ''),
  };
  const valueAxis = {
    axisLabel: { color: MUTED },
    splitLine: { lineStyle: { color: BORDER, type: 'dashed' as const } },
  };
  return {
    animation: false,
    textStyle: { fontFamily: 'Public Sans, system-ui, sans-serif', color: INK },
    legend: { top: 0, right: 0, textStyle: { color: MUTED } },
    grid: [
      { left: 64, right: 16, top: 36, height: '50%' },
      { left: 64, right: 16, top: '68%', bottom: 28 },
    ],
    axisPointer: { link: [{ xAxisIndex: 'all' }] },
    tooltip: {
      trigger: 'axis',
      confine: true,
      extraCssText: 'max-width: 360px; white-space: normal;',
      formatter: (params) => {
        const index = (Array.isArray(params) ? params[0] : params).dataIndex;
        const point = points[index];
        const last = index === points.length - 1;
        return weekReadout(point, last, periods.get(point.date), district, grain);
      },
    },
    xAxis: [
      {
        type: 'category',
        gridIndex: 0,
        data: dates,
        boundaryGap: false,
        axisLabel: { show: false },
      },
      { type: 'category', gridIndex: 1, data: dates, axisLabel, axisTick: { show: false } },
    ],
    yAxis: [
      { type: 'value', gridIndex: 0, name: 'Open', nameTextStyle: { color: MUTED }, ...valueAxis },
      {
        type: 'value',
        gridIndex: 1,
        name: grain === 'week' ? 'Per week' : 'Per day',
        nameTextStyle: { color: MUTED },
        ...valueAxis,
      },
    ],
    series: [
      {
        name: `Open at end of ${grain}`,
        type: 'line',
        xAxisIndex: 0,
        yAxisIndex: 0,
        data: points.map((p) => p.open),
        showSymbol: false,
        color: ACCENT,
        lineStyle: { width: 2 },
        areaStyle: { opacity: 0.18 },
      },
      {
        name: 'Clear-out',
        type: 'scatter',
        xAxisIndex: 0,
        yAxisIndex: 0,
        data: markers,
        symbol: 'diamond',
        symbolSize: 11,
        color: INK,
        itemStyle: { borderColor: '#FFFFFF', borderWidth: 1 },
        z: 3,
      },
      {
        name: 'Opened',
        type: 'bar',
        xAxisIndex: 1,
        yAxisIndex: 1,
        data: points.map((p) => p.opened),
        color: OPENED,
      },
      {
        name: 'Closed',
        type: 'bar',
        xAxisIndex: 1,
        yAxisIndex: 1,
        data: points.map((p) => p.closed),
        color: CLOSED,
      },
    ],
  };
}
