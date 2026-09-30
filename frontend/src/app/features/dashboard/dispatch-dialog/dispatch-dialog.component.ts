import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { ToastService } from '../../../core/services/ui.service';
import { Severity } from '../../../shared/models/api.models';
import { errorMessage } from '../../../shared/utils/format';

/**
 * Manual emergency dispatch (Reactive Form). POST /api/incidents/dispatch opens a Manual incident
 * and notifies responders immediately — the alert cooldown is bypassed for operator dispatches.
 */
@Component({
  selector: 'app-dispatch-dialog',
  imports: [ReactiveFormsModule],
  templateUrl: './dispatch-dialog.component.html',
})
export class DispatchDialogComponent {
  private readonly api = inject(MonitoringApiService);
  private readonly toast = inject(ToastService);
  protected readonly live = inject(SignalRService);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected readonly sending = signal(false);
  protected readonly severities: Severity[] = ['Critical', 'High', 'Medium'];
  protected readonly form = inject(FormBuilder).nonNullable.group({
    zone: ['', Validators.required],
    severity: ['Critical' as Severity, Validators.required],
    message: ['', Validators.maxLength(500)],
    activateSprinklers: [false],
  });

  open(): void {
    this.form.reset({ zone: this.live.zones()[0] ?? '', severity: 'Critical', message: '', activateSprinklers: false });
    this.dialog().nativeElement.showModal();
  }

  close(): void {
    this.dialog().nativeElement.close();
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.sending.set(true);
    const value = this.form.getRawValue();
    this.api.dispatch({ ...value, message: value.message.trim() || undefined }).subscribe({
      next: (incident) => {
        this.live.upsertIncident(incident);
        this.toast.show('Emergency dispatch sent', `Incident #${incident.id} · ${incident.zone}`, 'critical');
        this.sending.set(false);
        this.close();
      },
      error: (err) => {
        this.toast.show('Dispatch failed', errorMessage(err), 'error');
        this.sending.set(false);
      },
    });
  }
}
