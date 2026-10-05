import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { CategorySummaryResponse, CellStats } from '../api/models';
import { FilterStore } from '../filters/filter-store';
import { NeighborhoodDrawerComponent } from './neighborhood-drawer';

function stats(closed: number, bulkClosed: number, median: number, prior: number): CellStats {
  return {
    current: {
      from: '2026-07-07',
      to: '2026-10-04',
      opened: 118,
      closed,
      excluded: 2,
      bulkClosed,
      medianDays: median,
      p90Days: 810.78,
    },
    prior: {
      from: '2026-04-08',
      to: '2026-07-06',
      opened: 44,
      closed: 49,
      excluded: 5,
      bulkClosed: 0,
      medianDays: prior,
      p90Days: 37.47,
    },
    trend: { direction: 'slower', medianChangePct: 8820 },
    openBacklog: 85,
    medianOpenAgeDays: 120.5,
  };
}

// Pell/Main Industrial Park, 90 days, as of 2026-10-04.
const summary: CategorySummaryResponse = {
  windowDays: 90,
  district: null,
  neighborhood: { slug: 'pell-main-industrial-park', name: 'Pell/Main Industrial Park' },
  total: stats(129, 83, 704.68, 7.9),
  categories: [
    { category: 'Streets', stats: { ...stats(20, 0, 3.1, 2.9), trend: null } },
    { category: 'Parking', stats: stats(95, 83, 760.2, 9.4) },
  ],
  asOf: '2026-10-05T04:11:48Z',
};

describe('NeighborhoodDrawerComponent', () => {
  async function render(input: Partial<{ summary: CategorySummaryResponse; loading: boolean }>) {
    TestBed.configureTestingModule({ providers: [provideLocationMocks()] });
    const store = TestBed.inject(FilterStore);
    store.neighborhood.set('pell-main-industrial-park');
    const fixture = TestBed.createComponent(NeighborhoodDrawerComponent);
    fixture.componentRef.setInput('name', 'Pell/Main Industrial Park');
    fixture.componentRef.setInput('summary', input.summary);
    fixture.componentRef.setInput('loading', input.loading ?? false);
    await fixture.whenStable();
    return { fixture, page: fixture.nativeElement as HTMLElement, store };
  }

  it('compares the figures with the prior period', async () => {
    const { page } = await render({ summary });

    expect(page.querySelector('h2')?.textContent).toBe('Pell/Main Industrial Park');
    const compare = Array.from(page.querySelectorAll('.compare tbody tr')).map((tr) =>
      Array.from(tr.children).map((c) => c.textContent?.trim()),
    );
    expect(compare).toEqual([
      ['Median days to close', '705 days', '7.9 days'],
      ['90th percentile', '811 days', '37.5 days'],
      ['Closed', '129', '49'],
      ['Opened', '118', '44'],
      ['Closed, left out of timing', '2', '5'],
    ]);
    expect(page.querySelector('.trend')?.textContent).toBe(
      '8,820.0% slower than the prior 90 days',
    );
    expect(page.querySelector('.clear-out')?.textContent).toContain(
      '83 of these 129 closed requests were closed in a clear-out',
    );
  });

  it('lists categories by closed, and a name selects that category', async () => {
    const { fixture, page, store } = await render({ summary });

    const buttons = Array.from(
      page.querySelectorAll<HTMLButtonElement>('.table-wrap tbody .link-button'),
    );
    expect(buttons.map((b) => b.textContent?.trim())).toEqual(['Parking', 'Streets']);
    buttons[1].click();
    await fixture.whenStable();
    expect(store.category()).toBe('Streets');
    expect(page.querySelector('tr.selected th')?.textContent?.trim()).toBe('Streets');
  });

  it('closes on the button or Escape, clearing the selection', async () => {
    const { page, store } = await render({ summary });

    (page.querySelector('.close') as HTMLButtonElement).click();
    expect(store.neighborhood()).toBeNull();

    store.neighborhood.set('pell-main-industrial-park');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(store.neighborhood()).toBeNull();
  });

  it('shows the name while the figures load, and takes focus', async () => {
    const { page } = await render({ loading: true });

    expect(page.querySelector('h2')?.textContent).toBe('Pell/Main Industrial Park');
    expect(page.textContent).toContain('Loading…');
    expect(document.activeElement).toBe(page.querySelector('h2'));
  });
});
