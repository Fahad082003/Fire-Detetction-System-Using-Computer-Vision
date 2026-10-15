import { Point, homography, project } from './homography';

const close = (a: Point, b: Point) => { expect(a[0]).toBeCloseTo(b[0], 6); expect(a[1]).toBeCloseTo(b[1], 6); };

describe('homography', () => {
  // A camera looking at a 20 x 10 m floor at an angle: the far edge (y = 10 m) looks narrower in the frame.
  const image: Point[] = [[100, 600], [1100, 600], [900, 200], [300, 200]];
  const floor: Point[] = [[0, 0], [20, 0], [20, 10], [0, 10]];

  it('maps the 4 calibration points exactly', () => {
    const h = homography(image, floor)!;
    image.forEach((p, i) => close(project(h, p), floor[i]));
  });

  it('maps any other floor point with the same perspective', () => {
    const known = [0.9, 0.1, 3, -0.05, 1.1, 2, 0.0004, 0.0002, 1];
    const src: Point[] = [[0, 0], [640, 0], [640, 480], [0, 480]];
    const h = homography(src, src.map((p) => project(known, p)))!;
    close(project(h, [123, 321]), project(known, [123, 321]));
  });

  it('refuses points where 3 lie on one line', () => {
    expect(homography([[0, 0], [10, 0], [20, 0], [0, 10]], floor)).toBeNull();
  });
});
