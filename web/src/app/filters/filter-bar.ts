import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { CHART_RANGES, ChartRange, DISTRICTS, WINDOWS } from './filters';
import { FilterStore } from './filter-store';

/** A neighborhood to offer: its API slug and the boundary file's name. */
export interface NeighborhoodOption {
  slug: string;
  name: string;
}

/**
 * Window (segmented buttons), category, council district, neighborhood (opens its drawer) and the backlog chart's
 * range. Changes go straight to the {@link FilterStore}.
 */
@Component({
  selector: 'app-filter-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './filter-bar.html',
  styleUrl: './filter-bar.scss',
})
export class FilterBarComponent {
  protected readonly store = inject(FilterStore);

  /** Category groups to offer (service categories only). */
  readonly categories = input<readonly string[]>([]);
  /** Neighborhoods to offer, in display order. */
  readonly neighborhoods = input<readonly NeighborhoodOption[]>([]);

  /** A category from the URL that isn't in the list (yet): still shown, so the select matches the URL. */
  protected readonly unknownCategory = computed(() => {
    const category = this.store.category();
    return category !== null && !this.categories().includes(category) ? category : null;
  });

  /** Likewise a neighborhood slug from the URL that isn't in the list (yet). */
  protected readonly unknownNeighborhood = computed(() => {
    const slug = this.store.neighborhood();
    return slug !== null && !this.neighborhoods().some((n) => n.slug === slug) ? slug : null;
  });

  protected readonly windows = WINDOWS;
  protected readonly districts = DISTRICTS;
  protected readonly ranges = CHART_RANGES;

  protected onCategory(value: string): void {
    this.store.category.set(value === '' ? null : value);
  }

  protected onDistrict(value: string): void {
    this.store.district.set(value === '' ? null : Number(value));
  }

  protected onNeighborhood(value: string): void {
    this.store.neighborhood.set(value === '' ? null : value);
  }

  protected onRange(value: string): void {
    this.store.range.set(value as ChartRange);
  }
}
