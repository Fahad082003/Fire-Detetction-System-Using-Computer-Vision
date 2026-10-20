import { FrameStats } from '../../../core/services/signalr.service';
import { CameraFeed } from '../../../shared/models/api.models';
import { peopleCount } from './kpi-strip.component';

const cam = (id: string, locationZone: string) => ({ id, locationZone }) as CameraFeed;
const stat = (cameraId: string, humanCount: number, dangerCount = 0) => ({ cameraId, humanCount, dangerCount }) as FrameStats;

describe('peopleCount (Humans Tracked)', () => {
  const cameras = [cam('k1', 'First Floor - Kitchen'), cam('k2', 'First Floor - Kitchen'), cam('k3', 'First Floor - Kitchen'), cam('h1', 'First Floor - Hall')];

  it('counts people seen by several cameras of one room once (largest count, not the sum)', () => {
    expect(peopleCount([stat('k1', 2), stat('k2', 2), stat('k3', 2)], cameras)).toEqual({ humans: 2, danger: 0 });
    expect(peopleCount([stat('k1', 2), stat('k2', 1, 1), stat('k3', 2)], cameras)).toEqual({ humans: 2, danger: 1 }); // one camera's view blocked
  });

  it('adds different rooms, and counts a camera with no known zone on its own', () => {
    expect(peopleCount([stat('k1', 2), stat('k2', 2), stat('h1', 3), stat('unknown', 1)], cameras)).toEqual({ humans: 6, danger: 0 });
    expect(peopleCount([], cameras)).toEqual({ humans: 0, danger: 0 });
  });
});
