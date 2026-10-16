import { chartPath } from '../dashboard/incident-history/incident-history.component';
import { TestBed } from '@angular/core/testing';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { SignalRService } from '../../core/services/signalr.service';
import { ConfirmService, ToastService } from '../../core/services/ui.service';
import { PlanPointer } from '../../shared/components/floor-plan-map.component';
import { drawnRect, FloorPlansComponent } from './floor-plans.component';

describe('drawnRect (floor-plan drawing)', () => {
  it('normalises a drag in any direction and snaps to 0.1 m', () => {
    expect(drawnRect('Room', { x: 8.04, y: 6.02 }, { x: 2.01, y: 1.97 }, 30, 20)).toEqual({ x: 2, y: 2, width: 6, height: 4 });
  });

  it('clamps to the plan boundary', () => {
    expect(drawnRect('Room', { x: 25, y: 15 }, { x: 40, y: 30 }, 30, 20)).toEqual({ x: 25, y: 15, width: 5, height: 5 });
  });

  it('turns a wall drag into a 0.2 m thick bar along the drag direction', () => {
    expect(drawnRect('Wall', { x: 2, y: 5 }, { x: 12, y: 5.3 }, 30, 20)).toEqual({ x: 2, y: 4.9, width: 10, height: 0.2 });
    expect(drawnRect('Wall', { x: 5, y: 1 }, { x: 5.1, y: 9 }, 30, 20)).toEqual({ x: 4.9, y: 1, width: 0.2, height: 8 });
  });

  it('ignores a click without a drag', () => {
    expect(drawnRect('Room', { x: 3, y: 3 }, { x: 3.05, y: 3.05 }, 30, 20)).toBeNull();
  });
});

describe('chartPath (fire growth graph)', () => {
  it('returns null without data', () => expect(chartPath([])).toBeNull());

  it('scales time to the width and values to the height', () => {
    const c = chartPath([{ t: 0, v: 0 }, { t: 10, v: 10 }])!;
    expect(c.max).toBeCloseTo(11);
    expect(c.line).toBe('0.0,140.0 600.0,12.7');
  });
});

describe('FloorPlansComponent editing', () => {
  it('resizes from the cyan corner right after drawing, without switching to Select', () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: MonitoringApiService, useValue: {} }, { provide: SignalRService, useValue: {} },
        { provide: ToastService, useValue: {} }, { provide: ConfirmService, useValue: {} },
      ],
    });
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const c = TestBed.runInInjectionContext(() => new FloorPlansComponent()) as any;
    c.form.patchValue({ widthMeters: 30, heightMeters: 20 });
    const at = (kind: PlanPointer['kind'], x: number, y: number, handle = false) => c.onPointer({ kind, x, y, index: null, handle });
    c.tool.set('Room');
    at('down', 2, 2); at('move', 8, 6); at('up', 8, 6);
    at('down', 8, 6, true); at('move', 12, 9); at('up', 12, 9);
    expect(c.elements()).toHaveLength(1);
    expect(c.elements()[0]).toMatchObject({ x: 2, y: 2, width: 10, height: 7 });
  });
});
