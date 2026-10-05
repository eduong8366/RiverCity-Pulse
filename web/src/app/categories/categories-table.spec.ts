import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { CategorySummaryResponse, CellStats } from '../api/models';
import { FilterStore } from '../filters/filter-store';
import { CategoriesTableComponent, sortRows } from './categories-table';

function stats(opened: number, median: number | null, change: number | null): CellStats {
  return {
    current: {
      from: '2026-07-07',
      to: '2026-10-04',
      opened,
      closed: opened,
      excluded: 0,
      bulkClosed: 0,
      medianDays: median,
      p90Days: median,
    },
    prior: {
      from: '2026-04-08',
      to: '2026-07-06',
      opened,
      closed: opened,
      excluded: 0,
      bulkClosed: 0,
      medianDays: median,
      p90Days: median,
    },
    trend:
      change === null
        ? null
        : { direction: change > 0 ? 'slower' : 'faster', medianChangePct: change },
    openBacklog: opened,
    medianOpenAgeDays: median,
  };
}

const rows = [
  { category: 'Parking', stats: stats(5707, 532.63, 24000.9) },
  { category: 'Streets', stats: stats(9000, 4.2, -12.5) },
  { category: 'Measure O', stats: stats(3, null, null) },
];

const summary: CategorySummaryResponse = {
  windowDays: 90,
  district: null,
  neighborhood: null,
  total: stats(14710, 5.89, 21.4),
  categories: rows,
  asOf: '2026-10-05T04:11:48Z',
};

describe('sortRows', () => {
  it('sorts by a figure either way, with missing figures last', () => {
    const names = (key: Parameters<typeof sortRows>[1]) =>
      sortRows(rows, key).map((r) => r.category);

    expect(names({ key: 'median', descending: true })).toEqual(['Parking', 'Streets', 'Measure O']);
    expect(names({ key: 'median', descending: false })).toEqual([
      'Streets',
      'Parking',
      'Measure O',
    ]);
    expect(names({ key: 'change', descending: false })).toEqual([
      'Streets',
      'Parking',
      'Measure O',
    ]);
    expect(names({ key: 'category', descending: false })).toEqual([
      'Measure O',
      'Parking',
      'Streets',
    ]);
  });
});

describe('CategoriesTableComponent', () => {
  async function render() {
    TestBed.configureTestingModule({ providers: [provideLocationMocks()] });
    const fixture = TestBed.createComponent(CategoriesTableComponent);
    fixture.componentRef.setInput('summary', summary);
    await fixture.whenStable();
    return {
      fixture,
      page: fixture.nativeElement as HTMLElement,
      store: TestBed.inject(FilterStore),
    };
  }

  function names(page: HTMLElement): (string | undefined)[] {
    return Array.from(page.querySelectorAll('tbody th')).map((th) => th.textContent?.trim());
  }

  it('shows the total, then the categories by requests opened', async () => {
    const { page } = await render();

    expect(names(page)).toEqual(['All categories', 'Streets', 'Parking', 'Measure O']);
    const parking = Array.from(page.querySelectorAll('tbody tr'))[2];
    expect(Array.from(parking.querySelectorAll('td')).map((td) => td.textContent?.trim())).toEqual([
      '5,707',
      '5,707',
      '533 days',
      '533 days',
      '+24,000.9%',
      '5,707',
      '533 days',
      '–',
    ]);
  });

  it('sorts by a column header, flipping on a second click', async () => {
    const { fixture, page } = await render();
    const median = Array.from(page.querySelectorAll('thead button'))[3] as HTMLButtonElement;

    median.click();
    await fixture.whenStable();
    expect(names(page).slice(1)).toEqual(['Parking', 'Streets', 'Measure O']);
    expect(median.parentElement?.getAttribute('aria-sort')).toBe('descending');

    median.click();
    await fixture.whenStable();
    expect(names(page).slice(1)).toEqual(['Streets', 'Parking', 'Measure O']);
    expect(median.parentElement?.getAttribute('aria-sort')).toBe('ascending');
  });

  it('filters the dashboard to a category, and back', async () => {
    const { fixture, page, store } = await render();
    const streets = page.querySelector('tbody .link-button') as HTMLButtonElement;

    streets.click();
    await fixture.whenStable();
    expect(store.category()).toBe('Streets');
    expect(streets.getAttribute('aria-pressed')).toBe('true');

    streets.click();
    expect(store.category()).toBeNull();
  });
});
