import { TestBed } from '@angular/core/testing';
import { FreshnessResponse } from '../api/models';
import { FreshnessBadgeComponent } from './freshness-badge';

const run = {
  runId: 18,
  pipeline: 'Incremental',
  status: 'Succeeded',
  startedUtc: '2026-10-05T00:42:12.751Z',
  finishedUtc: '2026-10-05T00:42:15.395Z',
};

const stale: FreshnessResponse = {
  status: 'stale',
  maxAgeMinutes: 45,
  lastSuccess: run,
  lastRun: run,
  watermarkUtc: '2026-10-05T00:41:00Z',
  requestCount: 1575640,
  aggregatesAsOfDate: '2026-10-04',
  aggregatesRefreshedUtc: '2026-10-05T02:02:30.751Z',
  dq: { pass: 21, warn: 0, fail: 0, info: 1 },
};

describe('FreshnessBadgeComponent', () => {
  async function render(freshness: FreshnessResponse): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(FreshnessBadgeComponent);
    fixture.componentRef.setInput('freshness', freshness);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows stale data with the last run in Sacramento time and the request count', async () => {
    const badge = await render(stale);

    expect(badge.querySelector('.pill')?.textContent?.trim()).toBe('Stale');
    expect(badge.querySelector('.pill')?.classList).not.toContain('fresh');
    expect(badge.textContent).toContain('Updated Oct 4, 2026, 5:42 PM PDT');
    expect(badge.textContent).toContain('1,575,640 requests');
    expect(badge.textContent).not.toContain('data-quality');
  });

  it('shows fresh data and data-quality issues', async () => {
    const badge = await render({
      ...stale,
      status: 'fresh',
      dq: { pass: 19, warn: 2, fail: 1, info: 0 },
    });

    expect(badge.querySelector('.pill')?.textContent?.trim()).toBe('Fresh');
    expect(badge.textContent).toContain('3 data-quality issues');
    expect(badge.textContent).toContain('succeeded in the last 45 minutes');
  });
});
