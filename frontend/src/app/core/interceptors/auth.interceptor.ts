import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../services/auth.service';

/** Adds "Authorization: Bearer <token>" to every call to our API. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthService).token();
  const isApiCall = req.url.startsWith(`${environment.apiBaseUrl}/api/`);
  return next(token && isApiCall ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
};

/** A 401 from the API (except on the login call itself) means the session is over → back to the login page. */
export const unauthorizedInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !req.url.endsWith('/api/auth/login')) auth.logout();
      return throwError(() => err);
    }),
  );
};
