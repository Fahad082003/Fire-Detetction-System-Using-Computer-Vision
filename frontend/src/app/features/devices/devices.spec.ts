import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../core/services/auth.service';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { SignalRService } from '../../core/services/signalr.service';
import { CameraFeed } from '../../shared/models/api.models';
import { DevicesComponent } from './devices.component';

const cam = (id: string, name: string) => ({ id, name, feedType: 'CCTV', streamUrl: '', streamFormat: 'Simulated', locationZone: 'Ground Floor - Lobby', status: 'Online', updatedAt: '' }) as CameraFeed;

function setup(role: 'Admin' | 'Operator') {
  const cameras = signal<CameraFeed[]>([cam('cam-lobby', 'Main Lobby')]);
  const api = { createCamera: vi.fn((c) => of({ ...cam(c.id, c.name), locationZone: c.locationZone })), updateCamera: vi.fn(), deleteCamera: vi.fn() };
  TestBed.configureTestingModule({
    imports: [DevicesComponent],
    providers: [
      { provide: MonitoringApiService, useValue: api },
      { provide: SignalRService, useValue: { cameras, zones: signal(['Ground Floor - Lobby', 'Ground Floor - Warehouse']) } },
      { provide: AuthService, useValue: { user: signal({ role }) } },
    ],
  });
  const fixture = TestBed.createComponent(DevicesComponent);
  fixture.detectChanges();
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  return { fixture, api, cameras, c: fixture.componentInstance as any, el: fixture.nativeElement as HTMLElement };
}

describe('DevicesComponent', () => {
  it('lets an admin add a camera, which then shows in the list', () => {
    const { fixture, api, cameras, c, el } = setup('Admin');
    c.add();
    c.form.set({ id: 'cam-gate', name: 'Main Gate', feedType: 'CCTV', streamUrl: '', streamFormat: 'Simulated', locationZone: 'Ground Floor - Warehouse' });
    c.save();
    fixture.detectChanges();
    expect(api.createCamera).toHaveBeenCalledWith(expect.objectContaining({ id: 'cam-gate', locationZone: 'Ground Floor - Warehouse' }));
    expect(cameras().map((x) => x.id)).toEqual(['cam-lobby', 'cam-gate']);
    expect(el.textContent).toContain('Main Gate');
  });

  it('shows no edit buttons to an operator', () => {
    const { el } = setup('Operator');
    expect(el.textContent).toContain('Only an Admin can');
    expect(el.textContent).not.toContain('+ Camera');
  });
});
