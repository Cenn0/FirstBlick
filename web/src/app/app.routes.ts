import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { AuthService } from './core/auth.service';
import { modules } from './core/modules';

const authenticated: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.initialize();
  return auth.session() ? true : router.createUrlTree(['/login']);
};

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login.component').then(m => m.LoginComponent) },
  { path: 'overview', canActivate: [authenticated], loadComponent: () => import('./pages/dashboard.component').then(m => m.DashboardComponent) },
  ...modules.map(module => ({ path: module.kind, canActivate: [authenticated], data: { kind: module.kind }, loadComponent: () => import('./pages/collection.component').then(m => m.CollectionComponent) })),
  { path: 'technical', canActivate: [authenticated], loadComponent: () => import('./pages/technical.component').then(m => m.TechnicalComponent) },
  { path: 'settings', canActivate: [authenticated], loadComponent: () => import('./pages/settings.component').then(m => m.SettingsComponent) },
  { path: 'offline', canActivate: [authenticated], loadComponent: () => import('./pages/offline.component').then(m => m.OfflineComponent) },
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  { path: '**', loadComponent: () => import('./pages/not-found.component').then(m => m.NotFoundComponent) }
];
