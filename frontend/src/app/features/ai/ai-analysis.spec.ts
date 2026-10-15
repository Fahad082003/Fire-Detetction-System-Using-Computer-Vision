import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { AiStatus } from '../../shared/models/api.models';
import { AiAnalysisComponent } from './ai-analysis.component';

function setup(status: AiStatus) {
  const api = { aiStatus: () => of(status), analyzeImage: vi.fn() };
  TestBed.configureTestingModule({ imports: [AiAnalysisComponent], providers: [{ provide: MonitoringApiService, useValue: api }] });
  const fixture = TestBed.createComponent(AiAnalysisComponent);
  fixture.detectChanges();
  return { fixture, api, el: fixture.nativeElement as HTMLElement };
}

describe('AiAnalysisComponent', () => {
  it('shows OFFLINE and no detections when the AI service is down', () => {
    const { el } = setup({ configured: true, online: false, error: 'AI service unreachable at http://localhost:8008.', service: null });
    expect(el.textContent).toContain('OFFLINE');
    expect(el.textContent).toContain('unreachable');
    expect(el.querySelector('table')).toBeNull();
  });

  it('lists a missing model with its reason', () => {
    const { el } = setup({
      configured: true, online: true, error: null,
      service: {
        status: 'degraded', version: '1.0.0', capabilities: ['Human'], cameras: [], inference: { count: 0, avgMs: null, lastMs: null },
        models: [
          { name: 'person', file: 'yolox_tiny.onnx', license: 'Apache-2.0', categories: ['Human'], threshold: 0.45, loaded: true, error: null },
          { name: 'fire_smoke', file: 'fire_smoke_yolox.onnx', license: null, categories: ['Fire', 'Smoke'], threshold: 0.4, loaded: false, error: 'model file not found' },
        ],
      },
    });
    expect(el.textContent).toContain('some models missing');
    expect(el.textContent).toContain('model file not found');
  });

  it('rejects a non-image file in the browser without calling the API', () => {
    const { fixture, api, el } = setup({ configured: true, online: false, error: 'x', service: null });
    const input = el.querySelector('input[type=file]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [new File(['x'], 'notes.txt', { type: 'text/plain' })] });
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(api.analyzeImage).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Choose a PNG or JPG image.');
  });
});
