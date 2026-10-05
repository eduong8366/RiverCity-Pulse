import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { DashboardData } from './api/dashboard-data';
import { FilterBarComponent } from './filters/filter-bar';

@Component({
  selector: 'app-root',
  imports: [FilterBarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly data = inject(DashboardData);
}
