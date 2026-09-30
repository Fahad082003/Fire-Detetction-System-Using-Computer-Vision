import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { LoginResponse, UserDto } from '../../shared/models/api.models';

const STORAGE_KEY = 'sentinel.auth';

interface StoredSession { token: string; expiresAt: string; user: UserDto; }

/** Login / logout and the current JWT. The token lives in localStorage so a page refresh keeps you logged in. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly session = signal<StoredSession | null>(this.restore());

  readonly user = computed(() => this.session()?.user ?? null);
  readonly isLoggedIn = computed(() => {
    const s = this.session();
    return !!s && new Date(s.expiresAt).getTime() > Date.now();
  });

  /** Current bearer token, or null when logged out / expired. */
  token(): string | null {
    return this.isLoggedIn() ? this.session()!.token : null;
  }

  login(email: string, password: string): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiBaseUrl}/api/auth/login`, { email, password }).pipe(
      tap((res) => {
        const session: StoredSession = { token: res.token, expiresAt: res.expiresAt, user: res.user };
        try { localStorage.setItem(STORAGE_KEY, JSON.stringify(session)); } catch { /* storage unavailable */ }
        this.session.set(session);
      }),
    );
  }

  logout(redirect = true): void {
    try { localStorage.removeItem(STORAGE_KEY); } catch { /* storage unavailable */ }
    this.session.set(null);
    if (redirect) this.router.navigate(['/login']);
  }

  private restore(): StoredSession | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      const s = raw ? (JSON.parse(raw) as StoredSession) : null;
      return s && new Date(s.expiresAt).getTime() > Date.now() ? s : null;
    } catch {
      return null;
    }
  }
}
