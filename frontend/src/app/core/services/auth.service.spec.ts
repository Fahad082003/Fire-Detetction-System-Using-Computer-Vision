import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from './auth.service';

describe('AuthService', () => {
  afterEach(() => { vi.useRealTimers(); localStorage.clear(); });

  it('treats the token as expired once the clock passes expiresAt', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-15T10:00:00Z'));
    localStorage.setItem('sentinel.auth', JSON.stringify({ token: 't', expiresAt: '2026-10-15T10:00:01Z', user: { email: 'a@b.c' } }));
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideRouter([])] });
    const auth = TestBed.inject(AuthService);
    expect(auth.token()).toBe('t');

    vi.setSystemTime(new Date('2026-10-15T10:00:02Z'));
    expect(auth.isLoggedIn()).toBe(false);
    expect(auth.token()).toBeNull();
  });
});
