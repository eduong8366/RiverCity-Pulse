import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { PeriodStats, SlowerResponse } from '../api/models';
import { FilterStore } from '../filters/filter-store';
import { comparedSentence, SlowerPanelComponent } from './slower-panel';

function period(closed: number, bulkClosed: number, medianDays: number): PeriodStats {
  return {
    from: '2026-07-07',
    to: '2026-10-04',
    opened: closed,
    closed,
    excluded: 0,
    bulkClosed,
    medianDays,
    p90Days: medianDays * 3,
  };
}

// Live figures for the top two on 2026-10-04 (90 days).
const response: SlowerResponse = {
  by: 'neighborhood',
  windowDays: 90,
  category: null,
  district: null,
  compared: { slower: 76, steady: 11, faster: 34, noTrend: 8 },
  items: [
    {
      key: 'pell-main-industrial-park',
      name: 'Pell/Main Industrial Park',
      current: period(129, 83, 704.68),
      prior: period(49, 0, 7.9),
      daysAdded: 696.78,
      trend: { direction: 'slower', medianChangePct: 8820 },
      openBacklog: 85,
    },
    {
      key: 'greenbriar',
      name: 'Greenbriar',
      current: period(680, 0, 44.16),
      prior: period(403, 0, 5.15),
      daysAdded: 39.01,
      trend: { direction: 'slower', medianChangePct: 757.5 },
      openBacklog: 128,
    },
  ],
  asOf: '2026-10-05T04:11:48Z',
};

describe('comparedSentence', () => {
  it('says how all the compared neighborhoods split', () => {
    expect(comparedSentence(response)).toBe(
      '76 of 129 neighborhoods got slower against the prior 90 days, 34 faster, 11 steady; ' +
        '8 had under 30 closed in a period, too few to compare.',
    );
  });

  it('leaves out the too-few clause when every one had a trend', () => {
    const categories = {
      ...response,
      by: 'category' as const,
      compared: { slower: 7, steady: 2, faster: 6, noTrend: 0 },
    };
    expect(comparedSentence(categories)).toBe(
      '7 of 15 categories got slower against the prior 90 days, 6 faster, 2 steady.',
    );
  });
});

describe('SlowerPanelComponent', () => {
  async function render(r: SlowerResponse, byLocked = false) {
    TestBed.configureTestingModule({ providers: [provideLocationMocks()] });
    const fixture = TestBed.createComponent(SlowerPanelComponent);
    fixture.componentRef.setInput('response', r);
    fixture.componentRef.setInput('by', r.by);
    fixture.componentRef.setInput('byLocked', byLocked);
    await fixture.whenStable();
    return {
      fixture,
      page: fixture.nativeElement as HTMLElement,
      store: TestBed.inject(FilterStore),
    };
  }

  it('lists each one with its medians, days added and clear-out closures', async () => {
    const { page } = await render(response);

    const rows = Array.from(page.querySelectorAll('tbody tr')).map((tr) =>
      Array.from(tr.querySelectorAll('th, td')).map((c) => c.textContent?.trim()),
    );
    expect(rows).toEqual([
      ['Pell/Main Industrial Park', '705 days', '7.9 days', '+697 days', '129', '83'],
      ['Greenbriar', '44.2 days', '5.2 days', '+39.0 days', '680', '–'],
    ]);
  });

  it('opens a neighborhood, or selects a category', async () => {
    const { page, store } = await render(response);
    (page.querySelector('tbody .link-button') as HTMLButtonElement).click();
    expect(store.neighborhood()).toBe('pell-main-industrial-park');

    TestBed.resetTestingModule();
    const categories = await render({
      ...response,
      by: 'category',
      items: [{ ...response.items[0], key: 'Parking', name: 'Parking' }],
    });
    (categories.page.querySelector('tbody .link-button') as HTMLButtonElement).click();
    expect([categories.store.category(), categories.store.neighborhood()]).toEqual([
      'Parking',
      null,
    ]);
  });

  it('switches what it ranks, except categories while one is selected', async () => {
    const { fixture, page } = await render(response, true);
    const emitted: string[] = [];
    fixture.componentInstance.byChange.subscribe((by) => emitted.push(by));

    const [neighborhoods, categories] = Array.from(page.querySelectorAll('.segments button'));
    expect(neighborhoods.getAttribute('aria-pressed')).toBe('true');
    expect((categories as HTMLButtonElement).disabled).toBe(true);

    fixture.componentRef.setInput('byLocked', false);
    await fixture.whenStable();
    (categories as HTMLButtonElement).click();
    expect(emitted).toEqual(['category']);
  });

  it('says so when nothing got slower', async () => {
    const { page } = await render({ ...response, items: [] });

    expect(page.textContent).toContain('Nothing got slower by more than 5%');
    expect(page.querySelector('table')).toBeNull();
  });
});
