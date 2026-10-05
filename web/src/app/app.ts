import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DashboardData } from './api/dashboard-data';
import { BacklogChartComponent } from './chart/backlog-chart';
import { FilterBarComponent } from './filters/filter-bar';
import { FilterStore } from './filters/filter-store';
import { FreshnessBadgeComponent } from './freshness/freshness-badge';
import { CardContent, neighborhoodCard, summaryCard } from './map/card';
import { HowWeMeasureComponent } from './measure/how-we-measure';
import { NeighborhoodMapComponent } from './map/neighborhood-map';
import { StatsCardComponent } from './map/stats-card';

@Component({
  selector: 'app-root',
  imports: [
    BacklogChartComponent,
    FilterBarComponent,
    FreshnessBadgeComponent,
    HowWeMeasureComponent,
    NeighborhoodMapComponent,
    StatsCardComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly data = inject(DashboardData);
  private readonly filters = inject(FilterStore);

  protected readonly hovered = signal<string | null>(null);

  protected readonly mapNeighborhoods = computed(() =>
    this.data.map.hasValue() ? this.data.map.value().neighborhoods : [],
  );

  protected readonly backlogPoints = computed(() =>
    this.data.backlog.hasValue() ? this.data.backlog.value().points : [],
  );

  /** The hovered neighborhood's figures, else the selection's total (null until the summary loads). */
  protected readonly card = computed<CardContent | null>(() => {
    const filters = this.filters.filters();
    const slug = this.hovered();
    if (slug) {
      const feature = this.data.boundaries.hasValue()
        ? this.data.boundaries.value().features.find((f) => f.properties.slug === slug)
        : undefined;
      const figures = this.mapNeighborhoods().find((n) => n.slug === slug);
      return neighborhoodCard(figures?.name ?? feature?.properties.NAME ?? slug, figures, filters);
    }
    return this.data.summary.hasValue() ? summaryCard(this.data.summary.value(), filters) : null;
  });
}
