import { Component, ElementRef, effect, inject, viewChild } from '@angular/core';
import { ConfirmService } from '../../core/services/ui.service';

/** Native <dialog> driven by ConfirmService.ask(). Used before dangerous actions (e.g. master sprinkler switch). */
@Component({
  selector: 'app-confirm-dialog',
  template: `
    <dialog #dialog (close)="confirm.answer(false)" (cancel)="confirm.answer(false)"
      class="m-auto w-[min(400px,calc(100vw-32px))] rounded-2xl border border-ink-600 bg-ink-900 p-0 text-slate-100 shadow-2xl">
      @if (confirm.request(); as r) {
        <div class="flex flex-col gap-4 p-5">
          <h2 class="text-lg font-bold">{{ r.title }}</h2>
          <p class="text-sm text-slate-400">{{ r.text }}</p>
          <div class="flex justify-end gap-2">
            <button type="button" class="btn btn-ghost" (click)="confirm.answer(false)">Cancel</button>
            <button type="button" class="btn btn-danger" (click)="confirm.answer(true)">{{ r.confirmLabel }}</button>
          </div>
        </div>
      }
    </dialog>
  `,
})
export class ConfirmDialogComponent {
  protected readonly confirm = inject(ConfirmService);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const el = this.dialog().nativeElement;
      if (this.confirm.request() && !el.open) el.showModal();
      if (!this.confirm.request() && el.open) el.close();
    });
  }
}
