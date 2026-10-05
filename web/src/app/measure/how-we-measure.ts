import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ExclusionsResponse } from '../api/models';
import { formatCount, formatDate } from '../lib/format';

/**
 * "How we measure": what the figures cover, and everything they leave out with its count and reason
 * (/api/meta/exclusions), linked to the public definitions. There is deliberately no "as recorded" toggle.
 */
@Component({
  selector: 'app-how-we-measure',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './how-we-measure.html',
  styleUrl: './how-we-measure.scss',
})
export class HowWeMeasureComponent {
  readonly exclusions = input.required<ExclusionsResponse>();

  protected readonly count = formatCount;
  protected readonly date = formatDate;

  protected readonly current = computed(() => this.exclusions().current);
  /** One row per non-service type: the window's types, then any type only open now (nothing opened or closed lately). */
  protected readonly nonService = computed(() => {
    const open = new Map(this.exclusions().openNow.types.map((t) => [t.type, t] as const));
    const rows = this.current().nonService.types.map((t) => ({
      ...t,
      open: open.get(t.type)?.open ?? 0,
    }));
    const seen = new Set(rows.map((r) => r.type));
    for (const t of open.values()) {
      if (!seen.has(t.type)) {
        rows.push({ ...t, opened: 0, closed: 0 });
      }
    }
    return rows;
  });
}
