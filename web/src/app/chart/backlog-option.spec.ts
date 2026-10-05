import { BacklogPoint } from '../api/models';
import { backlogOption, escapeHtml, weekReadout } from './backlog-option';

const points: BacklogPoint[] = [
  { date: '2026-09-21', opened: 8000, closed: 7600, open: 46000 },
  { date: '2026-09-28', opened: 7900, closed: 8200, open: 45700 },
];

describe('backlogOption', () => {
  it('puts the open count on top and opened/closed bars below, on the same weeks', () => {
    const option = backlogOption(points);
    const series = option.series as {
      name: string;
      type: string;
      xAxisIndex: number;
      data: number[];
    }[];

    expect(series.map((s) => [s.name, s.type, s.xAxisIndex, s.data])).toEqual([
      ['Open at end of week', 'line', 0, [46000, 45700]],
      ['Opened', 'bar', 1, [8000, 7900]],
      ['Closed', 'bar', 1, [7600, 8200]],
    ]);
    const axes = option.xAxis as { data: string[] }[];
    expect(axes.map((a) => a.data)).toEqual([
      ['2026-09-21', '2026-09-28'],
      ['2026-09-21', '2026-09-28'],
    ]);
  });

  it('reads out a week, marking the last one as still running', () => {
    expect(weekReadout(points[0], false)).toContain('<strong>Week of Sep 21, 2026</strong>');
    expect(weekReadout(points[0], false)).toContain('<td class="num">46,000</td>');
    expect(weekReadout(points[1], true)).toContain('Week of Sep 28, 2026 (so far)');
  });

  it('escapes HTML', () => {
    expect(escapeHtml(`<b>"Tom's" & co</b>`)).toBe(
      '&lt;b&gt;&quot;Tom&#39;s&quot; &amp; co&lt;/b&gt;',
    );
  });
});
