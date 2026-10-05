import { MapNeighborhood } from '../api/models';

/** Neighborhoods with fewer closed requests than this are grey, as in the API's trend rule. */
export const MIN_CLOSED = 30;

/** Fast → slow (docs/design/decision.md). */
export const RAMP = ['#E3F1EE', '#AFD8D0', '#6DB6AC', '#2E8B83', '#0B5A56'] as const;
export const NO_DATA = '#DCE1E1';

/**
 * The cut points between `bins` quantile bins, interpolated like SQL's PERCENTILE_CONT. Quantiles rather than equal
 * widths because medians are heavily skewed (1 to 700+ days): equal widths would put almost everything in one bin.
 */
export function quantileCuts(values: readonly number[], bins: number = RAMP.length): number[] {
  const sorted = [...values].sort((a, b) => a - b);
  if (sorted.length === 0) {
    return [];
  }
  return Array.from({ length: bins - 1 }, (_, i) => {
    const rank = ((i + 1) / bins) * (sorted.length - 1);
    const low = Math.floor(rank);
    const high = Math.ceil(rank);
    return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
  });
}

/** The bin of `value`: how many cuts it is above (a value equal to a cut stays in the lower bin). */
export function binOf(value: number, cuts: readonly number[]): number {
  return cuts.filter((cut) => value > cut).length;
}

/** The neighborhoods that get a color: enough closed requests for a median. */
export function shaded(neighborhoods: readonly MapNeighborhood[]): MapNeighborhood[] {
  return neighborhoods.filter((n) => n.closed >= MIN_CLOSED && n.medianDays !== null);
}

/** Fill color for one neighborhood (undefined: not in the response, e.g. no requests in the district). */
export function fillFor(
  neighborhood: MapNeighborhood | undefined,
  cuts: readonly number[],
): string {
  if (!neighborhood || neighborhood.closed < MIN_CLOSED || neighborhood.medianDays === null) {
    return NO_DATA;
  }
  return RAMP[binOf(neighborhood.medianDays, cuts)];
}

/** Legend labels for the bins, in days: "Up to 3.8", "3.8 to 5.0", …, "Over 11.8". */
export function legendLabels(cuts: readonly number[]): string[] {
  if (cuts.length === 0) {
    return [];
  }
  const f = (x: number) => (x < 100 ? x.toFixed(1) : Math.round(x).toString());
  return [
    `Up to ${f(cuts[0])}`,
    ...cuts.slice(1).map((cut, i) => `${f(cuts[i])} to ${f(cut)}`),
    `Over ${f(cuts[cuts.length - 1])}`,
  ];
}
