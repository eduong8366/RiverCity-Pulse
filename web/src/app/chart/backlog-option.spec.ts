import { BacklogPoint, ClearOutNote } from '../api/models';
import { backlogOption, escapeHtml, weekReadout } from './backlog-option';

const points: BacklogPoint[] = [
  { date: '2026-09-21', opened: 8000, closed: 7600, open: 46000 },
  { date: '2026-09-28', opened: 7900, closed: 8200, open: 45700 },
];

const streets: ClearOutNote = {
  date: '2026-10-03',
  category: 'Streets',
  closed: 621,
  averageDaysToClose: 701.28,
  minutesSpanned: 135,
  isSweep: false,
  sweepCategories: [],
  note: 'On 2026-10-03, Streets closed 621 requests averaging 701 days old over 135 minutes.',
};

type Series = { name: string; type: string; xAxisIndex: number; data: unknown[] }[];

describe('backlogOption', () => {
  it('puts the open count on top and opened/closed bars below, on the same weeks', () => {
    const option = backlogOption(points);
    const series = option.series as Series;

    expect(series.map((s) => [s.name, s.type, s.xAxisIndex, s.data])).toEqual([
      ['Open at end of week', 'line', 0, [46000, 45700]],
      ['Clear-out', 'scatter', 0, []],
      ['Opened', 'bar', 1, [8000, 7900]],
      ['Closed', 'bar', 1, [7600, 8200]],
    ]);
    const axes = option.xAxis as { data: string[] }[];
    expect(axes.map((a) => a.data)).toEqual([
      ['2026-09-21', '2026-09-28'],
      ['2026-09-21', '2026-09-28'],
    ]);
  });

  it('marks the week of each clear-out on the open line', () => {
    const outside = { ...streets, date: '2026-08-10' }; // before the first point: no marker
    const series = backlogOption(points, [streets, outside]).series as Series;

    expect(series[1].data).toEqual([['2026-09-28', 45700]]);
  });

  it('reads out a week, marking the last one as still running', () => {
    expect(weekReadout(points[0], false)).toContain('<strong>Week of Sep 21, 2026</strong>');
    expect(weekReadout(points[0], false)).toContain('<td class="num">46,000</td>');
    expect(weekReadout(points[1], true)).toContain('Week of Sep 28, 2026 (so far)');
    expect(weekReadout(points[0], false)).not.toContain('Clear-outs');
  });

  it('by day, marks and reads out the clear-out on its own date', () => {
    const days: BacklogPoint[] = [
      { date: '2026-10-02', opened: 1200, closed: 1100, open: 45800 },
      { date: '2026-10-03', opened: 900, closed: 1500, open: 45200 },
    ];
    const option = backlogOption(days, [streets], null, 'day');
    const series = option.series as Series;

    expect(series[0].name).toBe('Open at end of day');
    expect(series[1].data).toEqual([['2026-10-03', 45200]]);
    const readout = weekReadout(days[1], true, [streets], null, 'day');
    expect(readout).toContain('<strong>Oct 3, 2026 (so far)</strong>');
    expect(readout).toContain('Open at end of day');
    expect(readout).toContain('<strong>Clear-outs on this day</strong>');
  });

  it('adds the notes of the week, saying they are citywide in a district view', () => {
    const city = weekReadout(points[1], true, [streets]);
    expect(city).toContain('<strong>Clear-outs this week</strong>');
    expect(city).toContain(`<li>${streets.note}</li>`);
    expect(city).toContain('Counted as recorded.');

    expect(weekReadout(points[1], true, [streets], 4)).toContain('Clear-outs this week (citywide)');
  });

  it('escapes HTML', () => {
    expect(escapeHtml(`<b>"Tom's" & co</b>`)).toBe(
      '&lt;b&gt;&quot;Tom&#39;s&quot; &amp; co&lt;/b&gt;',
    );
  });
});
