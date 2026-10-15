import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { FireRisk, FloorPlan, RouteResult } from '../../../shared/models/api.models';
import { EvacuationPanelComponent } from './evacuation-panel.component';

const noRisk = { active: false, message: '', cols: 0, rows: 0, cellMeters: 1, cells: [], areas: [], activeDangerZones: [] } as FireRisk;
const plan = (id: number, hasImage: boolean) => ({ id, name: `P${id}`, building: '', floorNumber: 0, widthMeters: 30, heightMeters: 20, hasImage, elements: [] }) as unknown as FloorPlan;

describe('EvacuationPanelComponent (plan switch)', () => {
  it('drops a late image and route of the plan the user already left', () => {
    const image = new Subject<Blob>(), route = new Subject<RouteResult>();
    URL.createObjectURL = () => 'blob:stale';
    TestBed.configureTestingModule({
      imports: [EvacuationPanelComponent],
      providers: [
        provideRouter([]),
        { provide: MonitoringApiService, useValue: { floorPlans: () => of([plan(1, true), plan(2, false)]), floorPlanImage: () => image, route: () => route, floorPlanRisk: () => of(noRisk) } },
        { provide: SignalRService, useValue: { openIncidents: signal([]), cameras: signal([]), detection$: new Subject() } },
      ],
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const c = TestBed.createComponent(EvacuationPanelComponent).componentInstance as any; // plan 1 auto-chosen, image pending
    c.onPointer({ kind: 'down', x: 5, y: 5 }); // route for plan 1 pending
    c.choose(2);
    image.next(new Blob());
    route.next({ found: true } as unknown as RouteResult);
    expect(c.imageUrl()).toBeNull();
    expect(c.result()).toBeNull();
    expect(c.routing()).toBe(false);
  });
});

describe('EvacuationPanelComponent (calibrated camera)', () => {
  it('places a detected person on the plan and routes from that dot', () => {
    const detection$ = new Subject<unknown>(), route = vi.fn(() => new Subject<RouteResult>());
    // Frame pixels (0..1000 x 0..500) map straight onto the 30 x 20 m plan, so the result is easy to check.
    const calibration = { floorPlanId: 1, imagePoints: [[0, 0], [1000, 0], [1000, 500], [0, 500]], planPoints: [[0, 0], [30, 0], [30, 20], [0, 20]] };
    TestBed.configureTestingModule({
      imports: [EvacuationPanelComponent],
      providers: [
        provideRouter([]),
        { provide: MonitoringApiService, useValue: { floorPlans: () => of([plan(1, false)]), route, floorPlanRisk: () => of(noRisk) } },
        { provide: SignalRService, useValue: { openIncidents: signal([]), cameras: signal([{ id: 'cam-1', calibration }]), detection$ } },
      ],
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const c = TestBed.createComponent(EvacuationPanelComponent).componentInstance as any;
    // Feet = bottom-centre of the box: (480 + 40/2, 150 + 100) px = (500, 250) px → (15, 10) m.
    detection$.next({ cameraId: 'cam-1', normalized: false, frameWidth: 1000, frameHeight: 500,
      detections: [{ category: 'Human', bbox: [480, 150, 40, 100] }, { category: 'Fire', bbox: [0, 0, 10, 10] }] });
    expect(c.people()).toHaveLength(1);
    expect(c.people()[0].x).toBeCloseTo(15);
    expect(c.people()[0].y).toBeCloseTo(10);

    c.onPointer({ kind: 'down', x: 15.4, y: 10.3 }); // click near the dot
    expect(c.start()).toEqual([15, 10]);
    expect(route).toHaveBeenCalledWith(1, 15, 10);
  });
});

describe('EvacuationPanelComponent (fire risk)', () => {
  it('lists the rooms the fire may reach next, nearest first, without the safe ones', () => {
    const risk: FireRisk = {
      active: true, message: 'Next at risk: Hall B (7.5 m from the fire, Medium).', cols: 2, rows: 1, cellMeters: 1, cells: [4, 2],
      activeDangerZones: ['Fire'],
      areas: [
        { name: 'Hall A', type: 'Room', level: 'Burning', distanceMeters: 0 },
        { name: 'Hall B', type: 'Room', level: 'Medium', distanceMeters: 7.5 },
        { name: 'Store', type: 'Room', level: 'None', distanceMeters: null },
      ],
    };
    TestBed.configureTestingModule({
      imports: [EvacuationPanelComponent],
      providers: [
        provideRouter([]),
        { provide: MonitoringApiService, useValue: { floorPlans: () => of([plan(1, false)]), floorPlanRisk: () => of(risk) } },
        { provide: SignalRService, useValue: { openIncidents: signal([]), cameras: signal([]), detection$: new Subject() } },
      ],
    });
    const fixture = TestBed.createComponent(EvacuationPanelComponent);
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Next at risk: Hall B');
    expect(text).toContain('Hall A Burning');
    expect(text).not.toContain('Store None');
  });
});
