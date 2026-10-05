import { TestBed } from '@angular/core/testing';
import { ClearOutNote, ExclusionsResponse } from '../api/models';
import { HowWeMeasureComponent } from './how-we-measure';

const info = 'Information call, answered during the call.';

// Trimmed from docs/design/sample/exclusions-90.json.
const exclusions: ExclusionsResponse = {
  windowDays: 90,
  current: {
    from: '2026-07-07',
    to: '2026-10-04',
    nonService: {
      opened: 24748,
      closed: 24848,
      types: [
        {
          categoryGroup: 'Other',
          type: 'Other',
          reason: 'Information and referral calls.',
          opened: 22998,
          closed: 23097,
        },
        {
          categoryGroup: 'Parking',
          type: 'Parking / General',
          reason: info,
          opened: 1750,
          closed: 1751,
        },
      ],
    },
    dateProblems: [
      { flag: 'ClosedMissingDate', description: 'Closed with no close date', closed: 7600 },
      { flag: 'InvalidCloseOrder', description: 'Closed before it was created', closed: 449 },
    ],
  },
  prior: {
    from: '2026-04-08',
    to: '2026-07-06',
    nonService: { opened: 0, closed: 0, types: [] },
    dateProblems: [],
  },
  openNow: {
    open: 41019,
    types: [
      {
        categoryGroup: 'Process/Unclassified',
        type: 'Review',
        reason: 'An inbox bucket.',
        open: 41000,
      },
      { categoryGroup: 'Parking', type: 'Parking / General', reason: info, open: 19 },
    ],
  },
  clearOutRule: {
    minCount: 100,
    sweepMinCount: 50,
    detectAgeDays: 180,
    memberAgeDays: 90,
    notes: '/api/meta/clear-outs',
  },
  definitions: 'https://github.com/eduong8366/RiverCity-Pulse/blob/main/docs/metrics.md',
  asOf: '2026-10-05T02:02:19.6Z',
};

describe('HowWeMeasureComponent', () => {
  async function render(
    clearOuts: ClearOutNote[] = [],
    category: string | null = null,
  ): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(HowWeMeasureComponent);
    fixture.componentRef.setInput('exclusions', exclusions);
    fixture.componentRef.setInput('clearOuts', clearOuts);
    fixture.componentRef.setInput('category', category);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  function note(date: string, category: string, closed: number): ClearOutNote {
    return {
      date,
      category,
      closed,
      averageDaysToClose: 500,
      minutesSpanned: 30,
      isSweep: false,
      sweepCategories: [],
      note: `On ${date}, ${category} closed ${closed} requests averaging 500 days old over 30 minutes.`,
    };
  }

  function notes(list: Element | null): string[] {
    return Array.from(list?.querySelectorAll('li') ?? []).map((li) => li.textContent?.trim() ?? '');
  }

  function rows(panel: HTMLElement, table: number): string[][] {
    return Array.from(panel.querySelectorAll('table')[table].querySelectorAll('tbody tr')).map(
      (tr) => Array.from(tr.children).map((cell) => cell.textContent?.trim() ?? ''),
    );
  }

  it('lists every non-service type with its reason and counts, including types only open now', async () => {
    const panel = await render();

    expect(panel.textContent).toContain(
      '24,748 opened and 24,848 closed in the last 90 days, and 41,019 open now',
    );
    expect(rows(panel, 0)).toEqual([
      ['Other', 'Information and referral calls.', '22,998', '23,097', '0'],
      ['Parking / General', info, '1,750', '1,751', '19'],
      ['Review', 'An inbox bucket.', '0', '0', '41,000'],
    ]);
  });

  it('lists the date problems and links the definitions', async () => {
    const panel = await render();

    expect(rows(panel, 1)).toEqual([
      ['Closed with no close date', '7,600'],
      ['Closed before it was created', '449'],
    ]);
    expect(panel.querySelector('a')?.getAttribute('href')).toBe(exclusions.definitions);
    expect(panel.textContent).toContain('Jul 7, 2026 to Oct 4, 2026');
  });

  it('lists the clear-outs in the window, with earlier ones folded away, and the rule', async () => {
    const recent = [note('2026-10-03', 'Parking', 621), note('2026-07-07', 'Parking', 104)];
    const earlier = note('2026-07-06', 'Parking', 300);
    const panel = await render([...recent, earlier], 'Parking');

    expect(panel.textContent).toContain(
      'a day when one category closed 100 or more requests older than 180 days, or a minute when 50 or more',
    );
    expect(panel.textContent).toContain(
      'Parking: 2 clear-outs in the last 90 days, closing 725 requests.',
    );
    expect(notes(panel.querySelector('.notes'))).toEqual(recent.map((n) => n.note));
    const details = panel.querySelector('details') as HTMLDetailsElement;
    expect(details.querySelector('summary')?.textContent).toContain(
      'Earlier clear-outs since Jan 1, 2024 (1)',
    );
    expect(notes(details)).toEqual([earlier.note]);
  });

  it('says when there are no clear-outs', async () => {
    const panel = await render([], null);

    expect(panel.textContent?.replace(/\s+/g, ' ')).toContain(
      'All categories: no clear-outs in the last 90 days.',
    );
    expect(panel.querySelector('details')).toBeNull();
  });
});
