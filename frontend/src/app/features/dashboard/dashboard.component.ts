import { Component } from '@angular/core';
import { ActivityFeedComponent } from './activity-feed/activity-feed.component';
import { AlertLogsComponent } from './alert-logs/alert-logs.component';
import { KpiStripComponent } from './kpi-strip/kpi-strip.component';
import { SensorPanelComponent } from './sensor-panel/sensor-panel.component';
import { VideoWallComponent } from './video-wall/video-wall.component';

/**
 * Command-center layout (responsive):
 *   wide screens  → left: video wall + alert logs; right: KPIs, sensors/sprinklers, event log
 *   tablets/phone → everything stacks in one column
 */
@Component({
  selector: 'app-dashboard',
  imports: [VideoWallComponent, KpiStripComponent, SensorPanelComponent, AlertLogsComponent, ActivityFeedComponent],
  template: `
    <main class="grid items-start gap-3.5 p-3 md:p-5 2xl:grid-cols-[minmax(0,1fr)_440px]">
      <div class="flex min-w-0 flex-col gap-3.5">
        <app-video-wall />
        <app-alert-logs />
      </div>
      <div class="flex min-w-0 flex-col gap-3.5">
        <app-kpi-strip />
        <app-sensor-panel />
        <app-activity-feed />
      </div>
    </main>
  `,
})
export class DashboardComponent {}
