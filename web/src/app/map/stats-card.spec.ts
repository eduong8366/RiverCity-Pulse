import { TestBed } from '@angular/core/testing';
import { CardContent } from './card';
import { StatsCardComponent } from './stats-card';

const downtown: CardContent = {
  title: 'Downtown',
  scope: 'All categories · Whole city · last 90 days',
  closed: 3264,
  bulkClosed: 1036,
  medianDays: 14.04,
  p90Days: 666.71,
  openBacklog: 1354,
  trend: { direction: 'slower', medianChangePct: 581.6 },
  windowDays: 90,
};

describe('StatsCardComponent', () => {
  async function render(
    content: CardContent,
    notes: string[] = [],
    more = 0,
  ): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(StatsCardComponent);
    fixture.componentRef.setInput('content', content);
    fixture.componentRef.setInput('notes', notes);
    fixture.componentRef.setInput('moreNotes', more);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the figures and the trend', async () => {
    const card = await render(downtown);

    const values = Array.from(card.querySelectorAll('dd')).map((d) => d.textContent?.trim());
    expect(values).toEqual(['14.0 days', '667 days', '3,264', '1,354']);
    const trend = card.querySelector('.trend') as HTMLElement;
    expect(trend.textContent).toBe('581.6% slower than the prior 90 days');
    expect(trend.classList).toContain('slower');
    expect(card.textContent).not.toContain('too few');
  });

  it('explains a grey neighborhood', async () => {
    const card = await render({ ...downtown, closed: 29, trend: null });

    expect(card.textContent).toContain('Under 30 closed: too few for a reliable median');
  });

  it('says how many closures were in a clear-out, with the notes it is given', async () => {
    const note =
      'On 2026-10-03, Streets closed 621 requests averaging 701 days old over 135 minutes.';
    const card = await render(downtown, [note], 2);

    const block = card.querySelector('.clear-out') as HTMLElement;
    expect(block.textContent).toContain(
      '1,036 of these 3,264 closed requests were closed in a clear-out of old requests, and are counted as recorded.',
    );
    expect(Array.from(block.querySelectorAll('li')).map((li) => li.textContent?.trim())).toEqual([
      note,
    ]);
    expect(block.textContent).toContain('2 more, and the rule that finds them, under');
  });

  it('has no clear-out block when the figures include none', async () => {
    const card = await render({ ...downtown, bulkClosed: 0 }, ['ignored']);

    expect(card.querySelector('.clear-out')).toBeNull();
  });
});
