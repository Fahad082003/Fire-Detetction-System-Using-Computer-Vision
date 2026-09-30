import { HttpErrorResponse } from '@angular/common/http';
import { errorMessage, relativeTime, systemLevel } from './format';

describe('systemLevel', () => {
  it('is nominal with nothing open', () => expect(systemLevel([], ['Normal'])).toBe('nominal'));
  it('is elevated for a Medium incident or a warning sensor', () => {
    expect(systemLevel(['Medium'], [])).toBe('elevated');
    expect(systemLevel([], ['Warning'])).toBe('elevated');
  });
  it('is critical for High+ incidents or alarming sensors', () => {
    expect(systemLevel(['High'], [])).toBe('critical');
    expect(systemLevel([], ['Alarm'])).toBe('critical');
  });
});

describe('relativeTime', () => {
  const now = Date.parse('2026-01-01T12:00:00Z');
  it('formats seconds, minutes and hours', () => {
    expect(relativeTime('2026-01-01T11:59:30Z', now)).toBe('30s ago');
    expect(relativeTime('2026-01-01T11:55:00Z', now)).toBe('5m ago');
    expect(relativeTime('2026-01-01T09:00:00Z', now)).toBe('3h ago');
    expect(relativeTime(null, now)).toBe('never');
  });
});

describe('errorMessage', () => {
  it('reads ASP.NET ProblemDetails validation errors', () => {
    const err = new HttpErrorResponse({ status: 400, error: { title: 'x', errors: { zone: ['The Zone field is required.'] } } });
    expect(errorMessage(err)).toBe('zone: The Zone field is required.');
  });
  it('explains a network failure', () => {
    expect(errorMessage(new HttpErrorResponse({ status: 0 }))).toContain('Cannot reach the API');
  });
});
