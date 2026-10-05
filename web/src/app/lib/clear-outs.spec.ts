import { ClearOutNote } from '../api/models';
import { bulkLine, byWeek, forCategory, inPeriod, weekStart } from './clear-outs';

function note(date: string, category: string, closed = 100): ClearOutNote {
  return {
    date,
    category,
    closed,
    averageDaysToClose: 500,
    minutesSpanned: 1,
    isSweep: false,
    sweepCategories: [],
    note: `On ${date}, ${category} closed ${closed} requests averaging 500 days old in one minute.`,
  };
}

const notes = [
  note('2026-10-03', 'Streets', 621),
  note('2026-09-02', 'Utility Billing', 162),
  note('2026-08-10', 'Parking', 10244),
  note('2026-09-02', 'Solid Waste', 84),
];

describe('clear-outs', () => {
  it('filters by category and period', () => {
    expect(forCategory(notes, 'parking').map((n) => n.date)).toEqual(['2026-08-10']);
    expect(forCategory(notes, null)).toHaveLength(4);
    expect(inPeriod(notes, '2026-09-02', '2026-10-02').map((n) => n.category)).toEqual([
      'Utility Billing',
      'Solid Waste',
    ]);
  });

  it('finds the Monday of the week', () => {
    expect(weekStart('2026-08-10')).toBe('2026-08-10'); // a Monday
    expect(weekStart('2026-10-04')).toBe('2026-09-28'); // a Sunday
    expect(weekStart('2026-09-02')).toBe('2026-08-31'); // a Wednesday
    expect(weekStart('2024-01-01')).toBe('2024-01-01');
  });

  it('groups by week', () => {
    const weeks = byWeek(notes);
    expect([...weeks.keys()]).toEqual(['2026-08-10', '2026-08-31', '2026-09-28']);
    expect(weeks.get('2026-08-31')?.map((n) => n.category)).toEqual([
      'Utility Billing',
      'Solid Waste',
    ]);
  });

  it('says how many closed requests were in a clear-out', () => {
    expect(bulkLine(3264, 1036)).toBe(
      '1,036 of these 3,264 closed requests were closed in a clear-out of old requests, and are counted as recorded.',
    );
    expect(bulkLine(40, 1)).toBe(
      '1 of these 40 closed requests was closed in a clear-out of old requests, and is counted as recorded.',
    );
    expect(bulkLine(3264, 0)).toBeNull();
  });
});
