import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { FreshnessResponse } from '../api/models';
import { formatCount, formatPacific } from '../lib/format';

/** "Fresh / Stale", the last successful run in Sacramento time and the request count, from /api/meta/freshness. */
@Component({
  selector: 'app-freshness-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './freshness-badge.html',
  styleUrl: './freshness-badge.scss',
})
export class FreshnessBadgeComponent {
  readonly freshness = input.required<FreshnessResponse>();

  protected readonly fresh = computed(() => this.freshness().status === 'fresh');
  protected readonly updated = computed(() =>
    formatPacific(this.freshness().lastSuccess?.finishedUtc),
  );
  protected readonly requests = computed(() => formatCount(this.freshness().requestCount));
  protected readonly issues = computed(() => this.freshness().dq.warn + this.freshness().dq.fail);
  protected readonly explanation = computed(
    () =>
      `Fresh means a data load from the city's feed succeeded in the last ` +
      `${this.freshness().maxAgeMinutes} minutes.`,
  );
}
