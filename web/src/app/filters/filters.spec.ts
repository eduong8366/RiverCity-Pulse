import { chartWindow, DEFAULT_FILTERS, Filters, filtersToQuery, parseFilters } from './filters';

describe('parseFilters', () => {
  it('reads window, category, district, neighborhood and chart range', () => {
    expect(
      parseFilters('?window=30&category=Solid%20Waste&district=4&neighborhood=Downtown&range=90d'),
    ).toEqual({
      window: 30,
      category: 'Solid Waste',
      district: 4,
      neighborhood: 'downtown',
      range: '90d',
    });
  });

  it('falls back to the defaults for missing or bad values', () => {
    expect(parseFilters('')).toEqual(DEFAULT_FILTERS);
    expect(
      parseFilters('?window=45&category=%20&district=9&neighborhood=%3Cb%3E&range=forever'),
    ).toEqual(DEFAULT_FILTERS);
    expect(parseFilters('?district=0').district).toBeNull();
  });
});

describe('filtersToQuery', () => {
  it('leaves out defaults', () => {
    expect(filtersToQuery(DEFAULT_FILTERS)).toBe('');
  });

  it('round-trips through parseFilters', () => {
    const filters: Filters = {
      window: 365,
      category: 'Homeless Camp',
      district: 7,
      neighborhood: 'central-oak-park',
      range: '1y',
    };
    expect(filtersToQuery(filters)).toBe(
      'window=365&category=Homeless+Camp&district=7&neighborhood=central-oak-park&range=1y',
    );
    expect(parseFilters(filtersToQuery(filters))).toEqual(filters);
  });
});

describe('chartWindow', () => {
  it('asks for everything by week by default', () => {
    expect(chartWindow('all', '2026-10-04')).toEqual({ from: null, grain: 'week' });
  });

  it('starts 12 months back on a Monday, so the first week is whole', () => {
    // 2025-10-04 was a Saturday; its week starts on Monday 2025-09-29.
    expect(chartWindow('1y', '2026-10-04')).toEqual({ from: '2025-09-29', grain: 'week' });
  });

  it('asks for the last 90 days, as-of day included, by day', () => {
    expect(chartWindow('90d', '2026-10-04')).toEqual({ from: '2026-07-07', grain: 'day' });
  });
});
