import { CategorySummaryResponse, CellStats } from '../api/models';
import { DEFAULT_FILTERS } from '../filters/filters';
import { neighborhoodCard, scopeLabel, summaryCard } from './card';

function stats(closed: number, medianDays: number, bulkClosed = 0): CellStats {
  const period = {
    from: '2026-07-07',
    to: '2026-10-04',
    opened: closed,
    closed,
    excluded: 0,
    bulkClosed,
    medianDays,
    p90Days: medianDays * 10,
  };
  return {
    current: period,
    prior: { ...period, bulkClosed: 0 },
    trend: { direction: 'steady', medianChangePct: 0 },
    openBacklog: 7,
    medianOpenAgeDays: 1,
  };
}

const summary: CategorySummaryResponse = {
  windowDays: 90,
  district: null,
  total: stats(122273, 5.89, 14689),
  categories: [{ category: 'Parking', stats: stats(9000, 12.5, 13000) }],
  asOf: '2026-10-05T02:02:19.6Z',
};

describe('card', () => {
  it('labels the scope', () => {
    expect(scopeLabel(DEFAULT_FILTERS)).toBe('All categories · Whole city · last 90 days');
    expect(scopeLabel({ window: 30, category: 'Parking', district: 4 })).toBe(
      'Parking · District 4 · last 30 days',
    );
  });

  it('shows the total, or the selected category, when nothing is hovered', () => {
    const total = summaryCard(summary, DEFAULT_FILTERS);
    expect([total.title, total.closed, total.medianDays, total.bulkClosed]).toEqual([
      'Citywide',
      122273,
      5.89,
      14689,
    ]);

    const parking = summaryCard(summary, { ...DEFAULT_FILTERS, category: 'parking', district: 3 });
    expect([parking.title, parking.closed, parking.medianDays]).toEqual(['District 3', 9000, 12.5]);
  });

  it('shows a neighborhood with no requests as empty', () => {
    const card = neighborhoodCard('Village 12', undefined, DEFAULT_FILTERS);
    expect([card.title, card.closed, card.medianDays, card.trend]).toEqual([
      'Village 12',
      0,
      null,
      null,
    ]);
  });
});
