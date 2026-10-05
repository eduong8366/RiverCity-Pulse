import { DEFAULT_FILTERS, filtersToQuery, parseFilters } from './filters';

describe('parseFilters', () => {
  it('reads window, category and district', () => {
    expect(parseFilters('?window=30&category=Solid%20Waste&district=4')).toEqual({
      window: 30,
      category: 'Solid Waste',
      district: 4,
    });
  });

  it('falls back to the defaults for missing or bad values', () => {
    expect(parseFilters('')).toEqual(DEFAULT_FILTERS);
    expect(parseFilters('?window=45&category=%20&district=9')).toEqual(DEFAULT_FILTERS);
    expect(parseFilters('?district=0').district).toBeNull();
  });
});

describe('filtersToQuery', () => {
  it('leaves out defaults', () => {
    expect(filtersToQuery(DEFAULT_FILTERS)).toBe('');
  });

  it('round-trips through parseFilters', () => {
    const filters = { window: 365, category: 'Homeless Camp', district: 7 } as const;
    expect(filtersToQuery(filters)).toBe('window=365&category=Homeless+Camp&district=7');
    expect(parseFilters(filtersToQuery(filters))).toEqual(filters);
  });
});
