import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { AuthService } from './core/auth.service';
import { modules } from './core/modules';

const authenticated: CanActivateFn = async (_route,state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.initialize();
  if(!auth.session())return router.createUrlTree(['/login']);
  if(auth.tenant()?.role==='Partner'&&!state.url.startsWith('/portal')&&!state.url.startsWith('/settings'))return router.createUrlTree(['/portal']);
  return true;
};

export const routes: Routes = [
  ...['activate','recover','reset'].map(mode=>({path:mode,data:{mode},loadComponent:()=>import('./pages/account.component').then(m=>m.AccountComponent)})),
  { path: 'login', loadComponent: () => import('./pages/login.component').then(m => m.LoginComponent) },
  { path: 'maintenance', canActivate: [authenticated], loadComponent: () => import('./pages/maintenance.component').then(m => m.MaintenanceComponent) },
  { path: 'overview', canActivate: [authenticated], loadComponent: () => import('./pages/dashboard.component').then(m => m.DashboardComponent) },
  { path: 'planning-week', canActivate: [authenticated], loadComponent: () => import('./pages/planning-week.component').then(m => m.PlanningWeekComponent) },
  { path: 'portal', canActivate: [authenticated], loadComponent: () => import('./pages/portal.component').then(m => m.PortalComponent) },
  { path: 'documents', canActivate: [authenticated], loadComponent: () => import('./pages/documents.component').then(m => m.DocumentsComponent) },
  { path: 'integrations', canActivate: [authenticated], loadComponent: () => import('./pages/integrations.component').then(m => m.IntegrationsComponent) },
  { path: 'trade-imports', canActivate: [authenticated], loadComponent: () => import('./pages/trade-imports.component').then(m => m.TradeImportsComponent) },
  { path: 'discovery', canActivate: [authenticated], loadComponent: () => import('./pages/discovery.component').then(m => m.DiscoveryComponent) },
  { path: 'quantity-links', canActivate: [authenticated], loadComponent: () => import('./pages/quantity-links.component').then(m => m.QuantityLinksComponent) },
  { path: 'inventory-control', canActivate: [authenticated], loadComponent: () => import('./pages/inventory-control.component').then(m => m.InventoryControlComponent) },
  { path: 'equipment-inspections', canActivate: [authenticated], loadComponent: () => import('./pages/equipment-workflow.component').then(m => m.EquipmentWorkflowComponent) },
  { path: 'vehicle-ledger', canActivate: [authenticated], loadComponent: () => import('./pages/vehicle-ledger.component').then(m => m.VehicleLedgerComponent) },
  { path: 'cost-analysis', canActivate: [authenticated], loadComponent: () => import('./pages/cost-analysis.component').then(m => m.CostAnalysisComponent) },
  ...['collections','collection-cases'].map(path=>({path,canActivate:[authenticated],loadComponent:()=>import('./pages/collections-workflow.component').then(m=>m.CollectionsWorkflowComponent)})),
  ...['form-definitions','form-submissions'].map(kind=>({path:kind,canActivate:[authenticated],data:{kind},loadComponent:()=>import('./pages/forms.component').then(m=>m.FormsComponent)})),
  {path:'roof-revisions',canActivate:[authenticated],loadComponent:()=>import('./pages/roof.component').then(m=>m.RoofComponent)},
  {path:'measurements',canActivate:[authenticated],loadComponent:()=>import('./pages/measurements.component').then(m=>m.MeasurementsComponent)},
  ...modules.filter(module=>!['documents','form-definitions','form-submissions','roof-revisions','measurements'].includes(module.kind)).map(module => ({ path: module.kind, canActivate: [authenticated], data: { kind: module.kind }, loadComponent: () => import('./pages/collection.component').then(m => m.CollectionComponent) })),
  { path: 'technical', canActivate: [authenticated], loadComponent: () => import('./pages/technical.component').then(m => m.TechnicalComponent) },
  { path: 'management', canActivate: [authenticated], loadComponent: () => import('./pages/management.component').then(m => m.ManagementComponent) },
  { path: 'operations', canActivate: [authenticated], loadComponent: () => import('./pages/operations.component').then(m => m.OperationsComponent) },
  { path: 'settings', canActivate: [authenticated], loadComponent: () => import('./pages/settings.component').then(m => m.SettingsComponent) },
  { path: 'offline', canActivate: [authenticated], loadComponent: () => import('./pages/offline.component').then(m => m.OfflineComponent) },
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  { path: '**', loadComponent: () => import('./pages/not-found.component').then(m => m.NotFoundComponent) }
];
