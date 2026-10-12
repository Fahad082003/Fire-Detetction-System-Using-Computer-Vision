import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/login/login.component').then((m) => m.LoginComponent),
    title: 'Sign in · Sentinel',
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./features/shell/shell.component').then((m) => m.ShellComponent),
    children: [
      {
        path: '',
        loadComponent: () => import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent),
        title: 'Command Center · Sentinel',
      },
      {
        path: 'floor-plans',
        loadComponent: () => import('./features/floor-plans/floor-plans.component').then((m) => m.FloorPlansComponent),
        title: 'Floor Plans · Sentinel',
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
