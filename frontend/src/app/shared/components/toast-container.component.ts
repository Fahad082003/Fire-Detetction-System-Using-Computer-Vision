import { Component, inject } from '@angular/core';
import { ToastService, ToastTone } from '../../core/services/ui.service';

const TONE_BORDER: Record<ToastTone, string> = {
  info: 'border-l-cyan-400', success: 'border-l-emerald-500', warning: 'border-l-amber-500',
  critical: 'border-l-red-500', error: 'border-l-red-500',
};

@Component({
  selector: 'app-toast-container',
  template: `
    <div class="fixed bottom-4 right-4 z-[60] flex w-[min(360px,calc(100vw-32px))] flex-col gap-2" aria-live="assertive">
      @for (t of toasts.toasts(); track t.id) {
        <button type="button" (click)="toasts.dismiss(t.id)"
          class="rounded-lg border border-ink-600 border-l-4 bg-ink-800 px-4 py-3 text-left text-sm shadow-xl shadow-black/50 {{ border(t.tone) }}"
          [attr.role]="t.tone === 'critical' ? 'alert' : 'status'">
          <strong class="block">{{ t.title }}</strong>
          @if (t.message) { <span class="text-slate-400">{{ t.message }}</span> }
        </button>
      }
    </div>
  `,
})
export class ToastContainerComponent {
  protected readonly toasts = inject(ToastService);
  protected border(tone: ToastTone): string { return TONE_BORDER[tone]; }
}
