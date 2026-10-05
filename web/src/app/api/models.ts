// Response shapes of the RiverCity Pulse API (src/Sac311.Api/Contracts.cs). Dates are ISO strings.
import type { Geometry } from 'geojson';

export type Direction = 'faster' | 'slower' | 'steady';

export interface Trend {
  direction: Direction;
  /** Null when the prior median was 0. */
  medianChangePct: number | null;
}

export interface PeriodStats {
  from: string;
  to: string;
  opened: number;
  closed: number;
  excluded: number;
  bulkClosed: number;
  medianDays: number | null;
  p90Days: number | null;
}

export interface CellStats {
  current: PeriodStats;
  prior: PeriodStats;
  trend: Trend | null;
  openBacklog: number;
  medianOpenAgeDays: number | null;
}

export interface CategorySummaryResponse {
  windowDays: number;
  district: number | null;
  neighborhood: { slug: string; name: string } | null;
  total: CellStats;
  categories: { category: string; stats: CellStats }[];
  asOf: string;
}

export interface MapNeighborhood {
  slug: string;
  name: string;
  opened: number;
  closed: number;
  bulkClosed: number;
  medianDays: number | null;
  p90Days: number | null;
  trend: Trend | null;
  openBacklog: number;
}

export interface MapResponse {
  windowDays: number;
  category: string | null;
  district: number | null;
  neighborhoods: MapNeighborhood[];
  asOf: string;
}

export interface BacklogPoint {
  date: string;
  opened: number;
  closed: number;
  open: number;
}

export interface BacklogResponse {
  from: string;
  to: string;
  grain: 'day' | 'week';
  category: string | null;
  district: number | null;
  points: BacklogPoint[];
  asOf: string;
}

export interface RunSummary {
  runId: number;
  pipeline: string;
  status: string;
  startedUtc: string;
  finishedUtc: string | null;
}

export interface FreshnessResponse {
  status: 'fresh' | 'stale';
  maxAgeMinutes: number;
  lastSuccess: RunSummary | null;
  lastRun: RunSummary | null;
  watermarkUtc: string | null;
  requestCount: number | null;
  aggregatesAsOfDate: string | null;
  aggregatesRefreshedUtc: string | null;
  dq: { pass: number; warn: number; fail: number; info: number };
}

export interface NonServiceType {
  categoryGroup: string;
  type: string;
  reason: string;
  opened: number;
  closed: number;
}

export interface DateProblemExclusion {
  flag: string;
  description: string;
  closed: number;
}

export interface ExclusionPeriod {
  from: string;
  to: string;
  nonService: { opened: number; closed: number; types: NonServiceType[] };
  dateProblems: DateProblemExclusion[];
}

export interface ClearOutRule {
  minCount: number;
  sweepMinCount: number;
  detectAgeDays: number;
  memberAgeDays: number;
  notes: string;
}

export interface ExclusionsResponse {
  windowDays: number;
  current: ExclusionPeriod;
  prior: ExclusionPeriod;
  openNow: {
    open: number;
    types: { categoryGroup: string; type: string; reason: string; open: number }[];
  };
  clearOutRule: ClearOutRule;
  definitions: string;
  asOf: string;
}

export interface ClearOutNote {
  date: string;
  category: string;
  closed: number;
  averageDaysToClose: number;
  minutesSpanned: number;
  isSweep: boolean;
  sweepCategories: string[];
  note: string;
}

export interface ClearOutsResponse {
  from: string;
  to: string;
  category: string | null;
  closed: number;
  clearOuts: ClearOutNote[];
  rule: ClearOutRule;
  definitions: string;
  asOf: string;
}

/** `/api/geo/neighborhoods`: the boundary file with each feature's API slug. */
export interface NeighborhoodBoundaries {
  type: 'FeatureCollection';
  features: {
    type: 'Feature';
    properties: { NAME: string; slug: string };
    geometry: Geometry;
  }[];
}

/** A neighborhood (`key` = slug) or category (`key` = its name) whose median went up; `daysAdded` ranks the list. */
export interface SlowerItem {
  key: string;
  name: string;
  current: PeriodStats;
  prior: PeriodStats;
  daysAdded: number;
  trend: Trend;
  openBacklog: number;
}

export type SlowerBy = 'neighborhood' | 'category';

export interface SlowerResponse {
  by: SlowerBy;
  windowDays: number;
  category: string | null;
  district: number | null;
  /** How all the compared neighborhoods or categories split (`noTrend`: under 30 closed in a period). */
  compared: { slower: number; steady: number; faster: number; noTrend: number };
  items: SlowerItem[];
  asOf: string;
}
