import { MapNeighborhood } from '../api/models';
import { binOf, fillFor, legendLabels, NO_DATA, quantileCuts, RAMP, shaded } from './bins';

function hood(slug: string, closed: number, medianDays: number | null): MapNeighborhood {
  return {
    slug,
    name: slug,
    opened: closed,
    closed,
    bulkClosed: 0,
    medianDays,
    p90Days: null,
    trend: null,
    openBacklog: 0,
  };
}

describe('bins', () => {
  it('interpolates cuts like PERCENTILE_CONT', () => {
    // 0..10: the 20th/40th/60th/80th percentiles fall on 2, 4, 6, 8.
    expect(quantileCuts([10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0])).toEqual([2, 4, 6, 8]);
    // Between values: rank 0.2 * 3 = 0.6, so 1 + 0.6 * (2 - 1).
    expect(quantileCuts([1, 2, 3, 4])[0]).toBeCloseTo(1.6);
    expect(quantileCuts([])).toEqual([]);
  });

  it('puts a value on a cut in the lower bin', () => {
    const cuts = [2, 4, 6, 8];
    expect([0, 2, 2.01, 8, 9].map((v) => binOf(v, cuts))).toEqual([0, 0, 1, 3, 4]);
  });

  it('shades only neighborhoods with 30+ closed and a median', () => {
    const cuts = [2, 4, 6, 8];
    const hoods = [hood('a', 30, 1), hood('b', 29, 1), hood('c', 40, null)];
    expect(shaded(hoods).map((n) => n.slug)).toEqual(['a']);
    expect(fillFor(hood('a', 30, 9), cuts)).toBe(RAMP[4]);
    expect(fillFor(hood('b', 29, 9), cuts)).toBe(NO_DATA);
    expect(fillFor(undefined, cuts)).toBe(NO_DATA);
  });

  it('labels the legend', () => {
    // The 90-day cuts of 2026-10-04 (docs/design/decision.md).
    expect(legendLabels([3.828, 4.974, 7.122, 11.756])).toEqual([
      'Up to 3.8',
      '3.8 to 5.0',
      '5.0 to 7.1',
      '7.1 to 11.8',
      'Over 11.8',
    ]);
  });
});
