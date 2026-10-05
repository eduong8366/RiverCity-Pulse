import { formatCount, formatDate, formatDays, formatPacific, formatTrend } from './format';

describe('format', () => {
  it('formats counts and days', () => {
    expect(formatCount(122273)).toBe('122,273');
    expect(formatCount(null)).toBe('–');
    expect(formatDays(5.89)).toBe('5.9 days');
    expect(formatDays(1)).toBe('1.0 day');
    expect(formatDays(327.98)).toBe('328 days');
    expect(formatDays(1704.4)).toBe('1,704 days');
    expect(formatDays(null)).toBe('–');
  });

  it('describes the trend', () => {
    expect(formatTrend({ direction: 'slower', medianChangePct: 21.4 }, 90)).toBe(
      '21.4% slower than the prior 90 days',
    );
    expect(formatTrend({ direction: 'faster', medianChangePct: -12.4 }, 30)).toBe(
      '12.4% faster than the prior 30 days',
    );
    expect(formatTrend({ direction: 'slower', medianChangePct: 24001.43 }, 90)).toBe(
      '24,001.4% slower than the prior 90 days',
    );
    expect(formatTrend({ direction: 'steady', medianChangePct: 2 }, 365)).toContain('Steady');
    expect(formatTrend(null, 90)).toBe('No trend: under 30 closed in a period');
  });

  it('shows times in Sacramento time', () => {
    expect(formatPacific('2026-10-05T00:42:15.395Z')).toBe('Oct 4, 2026, 5:42 PM PDT');
    expect(formatPacific('2026-01-05T20:00:00Z')).toBe('Jan 5, 2026, 12:00 PM PST');
    expect(formatDate('2026-08-10')).toBe('Aug 10, 2026');
  });
});
