import { DecimalPipe } from '@angular/common';
import { Component, OnDestroy, inject, signal } from '@angular/core';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { AiAnalysis, AiStatus, Detection } from '../../shared/models/api.models';
import { OVERLAY_COLORS } from '../dashboard/video-feed/overlay-renderer';
import { errorMessage } from '../../shared/utils/format';

const MAX_BYTES = 5 * 1024 * 1024;

/**
 * AI inference service status and a test-image check (requirements 1.1, 2.1, 3.1, 6.3).
 * Results come from the real models in ai-service; nothing is shown when the service or a model is missing.
 */
@Component({
  selector: 'app-ai-analysis',
  imports: [DecimalPipe],
  templateUrl: './ai-analysis.component.html',
})
export class AiAnalysisComponent implements OnDestroy {
  private readonly api = inject(MonitoringApiService);

  protected readonly status = signal<AiStatus | null>(null);
  protected readonly statusError = signal<string | null>(null);
  protected readonly imageUrl = signal<string | null>(null);
  protected readonly result = signal<AiAnalysis | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly color = (d: Detection) => (OVERLAY_COLORS as Record<string, string>)[d.category.toLowerCase()] ?? OVERLAY_COLORS.other;

  constructor() {
    this.refresh();
  }

  ngOnDestroy(): void {
    this.setImage(null);
  }

  protected refresh(): void {
    this.api.aiStatus().subscribe({
      next: (s) => { this.status.set(s); this.statusError.set(null); },
      error: (err) => this.statusError.set(errorMessage(err)),
    });
  }

  protected onFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.result.set(null);
    if (!['image/png', 'image/jpeg'].includes(file.type)) { this.error.set('Choose a PNG or JPG image.'); return; }
    if (file.size > MAX_BYTES) { this.error.set('Image too large: maximum size is 5 MB.'); return; }
    this.error.set(null);
    this.setImage(URL.createObjectURL(file));
    this.busy.set(true);
    this.api.analyzeImage(file).subscribe({
      next: (r) => { this.result.set(r); this.busy.set(false); },
      error: (err) => { this.error.set(errorMessage(err)); this.busy.set(false); },
    });
  }

  private setImage(url: string | null): void {
    const old = this.imageUrl();
    if (old) URL.revokeObjectURL(old);
    this.imageUrl.set(url);
  }
}
