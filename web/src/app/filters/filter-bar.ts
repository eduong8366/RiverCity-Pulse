import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DISTRICTS, WINDOWS } from './filters';
import { FilterStore } from './filter-store';

/** Window (segmented buttons), category and council district. Changes go straight to the {@link FilterStore}. */
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

  /** A category from the URL that isn't in the list (yet): still shown, so the select matches the URL. */
  protected readonly unknownCategory = computed(() => {
    const category = this.store.category();
    return category !== null && !this.categories().includes(category) ? category : null;
  });

  protected readonly windows = WINDOWS;
  protected readonly districts = DISTRICTS;

  protected onCategory(value: string): void {
    this.store.category.set(value === '' ? null : value);
  }

  protected onDistrict(value: string): void {
    this.store.district.set(value === '' ? null : Number(value));
  }
}
