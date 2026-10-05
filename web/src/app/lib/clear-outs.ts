import { ClearOutNote } from '../api/models';

/** The clear-outs of one category (any case), or all of them when `category` is null. */
export function forCategory(
  notes: readonly ClearOutNote[],
  category: string | null,
): ClearOutNote[] {
  const wanted = category?.toLowerCase();
  return notes.filter((n) => wanted === undefined || n.category.toLowerCase() === wanted);
}

/** The clear-outs dated from `from` to `to` (ISO dates, inclusive). */
export function inPeriod(notes: readonly ClearOutNote[], from: string, to: string): ClearOutNote[] {
  return notes.filter((n) => n.date >= from && n.date <= to);
}

/** The Monday on or before an ISO date: the label of its week in /api/backlog. */
export function weekStart(iso: string): string {
  const day = new Date(`${iso}T00:00:00Z`);
  day.setUTCDate(day.getUTCDate() - ((day.getUTCDay() + 6) % 7));
  return day.toISOString().slice(0, 10);
}

/** Clear-outs grouped by the week they fall in, oldest first within a week. */
export function byWeek(notes: readonly ClearOutNote[]): Map<string, ClearOutNote[]> {
  const weeks = new Map<string, ClearOutNote[]>();
  for (const note of [...notes].sort((a, b) => a.date.localeCompare(b.date))) {
    const week = weekStart(note.date);
    weeks.set(week, [...(weeks.get(week) ?? []), note]);
  }
  return weeks;
}

/**
 * The hover-card line for figures that include clear-outs, or null when they don't. Factual only: how many of the
 * closed requests were in one, and that they are counted.
 */
export function bulkLine(closed: number, bulkClosed: number): string | null {
  if (bulkClosed <= 0) {
    return null;
  }
  const count = new Intl.NumberFormat('en-US');
  const [was, are] = bulkClosed === 1 ? ['was', 'is'] : ['were', 'are'];
  return (
    `${count.format(bulkClosed)} of these ${count.format(closed)} closed requests ${was} closed ` +
    `in a clear-out of old requests, and ${are} counted as recorded.`
  );
}
