import { chartPath } from '../dashboard/incident-history/incident-history.component';
import { drawnRect } from './floor-plans.component';

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
