import { Trend } from '../api/models';

const counts = new Intl.NumberFormat('en-US');

/** 12,345 */
export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? '–' : counts.format(value);
}

/** Days with one decimal under 100 ("5.9 days"), whole days above ("328 days"). */
export function formatDays(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return '–';
  }
  const text = value < 100 ? value.toFixed(1) : counts.format(Math.round(value));
  return `${text} ${text === '1.0' ? 'day' : 'days'}`;
}

/** "21.4% slower than the prior 90 days", or why there's no trend. */
export function formatTrend(trend: Trend | null, windowDays: number): string {
  if (trend === null) {
    return 'No trend: under 30 closed in a period';
  }
  if (trend.direction === 'steady') {
    return `Steady (within 5% of the prior ${windowDays} days)`;
  }
  if (trend.medianChangePct === null) {
    // Only a slower trend can start from a median of 0.
    return `Slower than the prior ${windowDays} days (up from a median of 0)`;
  }
  const pct = Math.abs(trend.medianChangePct).toLocaleString('en-US', {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
  });
  return `${pct}% ${trend.direction} than the prior ${windowDays} days`;
}

const pacific = new Intl.DateTimeFormat('en-US', {
  timeZone: 'America/Los_Angeles',
  month: 'short',
  day: 'numeric',
  year: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  timeZoneName: 'short',
});

/** A UTC timestamp in Sacramento time: "Oct 4, 2026, 5:42 PM PDT". */
export function formatPacific(utc: string | null | undefined): string {
  return utc ? pacific.format(new Date(utc)) : '–';
}

const calendarDay = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  month: 'short',
  day: 'numeric',
  year: 'numeric',
});

/** An ISO date ("2026-08-10") as "Aug 10, 2026". */
export function formatDate(iso: string): string {
  return calendarDay.format(new Date(`${iso}T00:00:00Z`));
}
