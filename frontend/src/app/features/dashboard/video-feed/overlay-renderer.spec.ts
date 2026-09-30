import { contentRect, toUnitBox } from './overlay-renderer';

describe('overlay geometry', () => {
  it('letterboxes a 16:9 frame inside a square stage (bars top and bottom)', () => {
    expect(contentRect(400, 400, 1920, 1080)).toEqual({ x: 0, y: 87.5, w: 400, h: 225 });
  });

  it('pillarboxes a 4:3 frame inside a 16:9 stage (bars left and right)', () => {
    const r = contentRect(1600, 900, 640, 480);
    expect(r.h).toBe(900);
    expect(r.w).toBe(1200);
    expect(r.x).toBe(200);
  });

  it('fills the stage when the source size is unknown', () => {
    expect(contentRect(300, 200, 0, 0)).toEqual({ x: 0, y: 0, w: 300, h: 200 });
  });

  it('converts pixel boxes to 0..1 and passes normalized boxes through', () => {
    expect(toUnitBox([640, 360, 128, 72], { normalized: false, frameWidth: 1280, frameHeight: 720 })).toEqual([0.5, 0.5, 0.1, 0.1]);
    expect(toUnitBox([0.1, 0.2, 0.3, 0.4], { normalized: true, frameWidth: 1, frameHeight: 1 })).toEqual([0.1, 0.2, 0.3, 0.4]);
  });
});
