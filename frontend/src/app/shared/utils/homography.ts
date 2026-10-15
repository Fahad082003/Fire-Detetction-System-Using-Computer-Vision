export type Point = [number, number];

/**
 * Perspective transform (homography) that maps 4 camera-frame points onto the same 4 floor-plan points.
 * Returns the 3x3 matrix row-major (last entry 1), or null when 3 of the points lie on one line.
 * Valid only for points on the floor plane, so callers map a person's feet, not the head.
 */
export function homography(src: Point[], dst: Point[]): number[] | null {
  // u = (h0x + h1y + h2) / (h6x + h7y + 1), v = (h3x + h4y + h5) / (h6x + h7y + 1): 2 linear equations per pair.
  const a = src.flatMap(([x, y], i) => {
    const [u, v] = dst[i];
    return [[x, y, 1, 0, 0, 0, -u * x, -u * y, u], [0, 0, 0, x, y, 1, -v * x, -v * y, v]];
  });
  for (let c = 0; c < 8; c++) { // Gauss-Jordan elimination with partial pivoting
    let p = c;
    for (let r = c + 1; r < 8; r++) if (Math.abs(a[r][c]) > Math.abs(a[p][c])) p = r;
    if (Math.abs(a[p][c]) < 1e-12) return null;
    [a[c], a[p]] = [a[p], a[c]];
    for (let r = 0; r < 8; r++) {
      if (r === c) continue;
      const f = a[r][c] / a[c][c];
      for (let k = c; k < 9; k++) a[r][k] -= f * a[c][k];
    }
  }
  return [...a.map((row, i) => row[8] / row[i]), 1];
}

export function project(h: number[], [x, y]: Point): Point {
  const w = h[6] * x + h[7] * y + h[8];
  return [(h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w];
}
