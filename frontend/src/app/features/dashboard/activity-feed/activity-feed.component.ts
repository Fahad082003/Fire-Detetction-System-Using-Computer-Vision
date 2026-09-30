import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { SignalRService } from '../../../core/services/signalr.service';
import { ActivityLevel } from '../../../shared/models/api.models';

const DOT: Record<ActivityLevel, string> = {
  info: 'bg-slate-500', success: 'bg-emerald-500', warning: 'bg-amber-500', critical: 'bg-red-500', notify: 'bg-cyan-400',
};

/** Live event log: incident changes, sensor state changes, sprinkler actions and notification results. */
@Component({
  selector: 'app-activity-feed',
  imports: [DatePipe],
  template: `
    <section class="panel flex max-h-[520px] flex-col" aria-labelledby="activity-title">
      <header class="panel-head">
        <h2 id="activity-title" class="panel-title">Live Event Log</h2>
        <button type="button" class="btn btn-ghost btn-sm" (click)="live.clearActivity()">Clear</button>
      </header>
      <ol class="flex-1 overflow-y-auto py-1.5 text-[12.5px]" aria-live="polite">
        @for (a of live.activity(); track $index) {
          <li class="grid grid-cols-[60px_8px_minmax(0,1fr)] items-baseline gap-2.5 border-l-2 px-4 py-1.5"
              [class]="a.level === 'critical' ? 'border-l-red-500 bg-red-500/10 font-semibold text-slate-100' : 'border-l-transparent text-slate-300'">
            <time class="font-mono text-[11px] text-slate-500">{{ a.timestamp | date: 'HH:mm:ss' }}</time>
            <span class="h-1.5 w-1.5 self-center rounded-full {{ dot(a.level) }}"></span>
            <span class="break-words">{{ a.message }} @if (a.zone) { <span class="ml-1 text-[11px] text-slate-500">{{ a.zone }}</span> }</span>
          </li>
        } @empty {
          <li class="p-5 text-center text-slate-500">Waiting for events…</li>
        }
      </ol>
    </section>
  `,
})
export class ActivityFeedComponent {
  protected readonly live = inject(SignalRService);
  protected dot(level: ActivityLevel): string { return DOT[level] ?? DOT.info; }
}
