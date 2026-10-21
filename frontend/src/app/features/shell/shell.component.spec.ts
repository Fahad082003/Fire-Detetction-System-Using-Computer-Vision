import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { AuthService } from '../../core/services/auth.service';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { PushNotificationService } from '../../core/services/push-notification.service';
import { SignalRService } from '../../core/services/signalr.service';
import { AlarmService } from '../../core/services/ui.service';
import { AiStatus } from '../../shared/models/api.models';
import { ShellComponent } from './shell.component';

const ok = { configured: true, online: true, error: null, service: { status: 'ok', cameras: [] } } as unknown as AiStatus;
const down = { configured: true, online: false, error: 'unreachable', service: null } as AiStatus;

describe('ShellComponent AI status polling', () => {
  it('ignores a slow older reply that arrives after a newer one', () => {
    const replies: Subject<AiStatus>[] = [];
    TestBed.configureTestingModule({
      providers: [
        { provide: MonitoringApiService, useValue: { aiStatus: () => { const r = new Subject<AiStatus>(); replies.push(r); return r; } } },
        { provide: SignalRService, useValue: { state: signal('connected'), now: signal(Date.now()), cameras: signal([]), frameStats: signal({}),
          openIncidents: signal([]), level: signal('nominal') } },
        { provide: AuthService, useValue: {} }, { provide: AlarmService, useValue: {} }, { provide: PushNotificationService, useValue: {} },
      ],
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const c = TestBed.runInInjectionContext(() => new ShellComponent()) as any;
    c.pollAi(); c.pollAi();                  // poll 1 is slow, poll 2 is newer
    replies[1].next(down);                   // newer reply first: AI offline
    expect(c.levelLabel()).toBe('MONITORING DEGRADED · AI OFFLINE');
    replies[0].next(ok);                     // the older "ok" arrives late
    expect(c.levelLabel()).toBe('MONITORING DEGRADED · AI OFFLINE');
    replies[1].complete(); c.pollAi(); replies[2].next(ok);
    expect(c.levelLabel()).toBe('ALL SYSTEMS NOMINAL');
  });
});
