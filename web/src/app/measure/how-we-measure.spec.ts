import { TestBed } from '@angular/core/testing';
import { ExclusionsResponse } from '../api/models';
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
  async function render(): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(HowWeMeasureComponent);
    fixture.componentRef.setInput('exclusions', exclusions);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
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
});
