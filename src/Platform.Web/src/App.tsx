import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { Layout } from '@/components/shell/Layout';
import { CatalogPage } from '@/app/catalog/CatalogPage';
import { RequestPage } from '@/app/catalog/RequestPage';
import { RequestsPage } from '@/app/requests/RequestsPage';
import { RequestDetailPage } from '@/app/requests/RequestDetailPage';
import { ApprovalsPage } from '@/app/approvals/ApprovalsPage';
import { ApprovalDetailPage } from '@/app/approvals/ApprovalDetailPage';
import { DeploymentsPage } from '@/app/deployments/DeploymentsPage';
import { ProductDeploymentsPage } from '@/app/deployments/ProductDeploymentsPage';
import { DeploymentHistoryPage } from '@/app/deployments/DeploymentHistoryPage';
import { DeploymentDetailPage } from '@/app/deployments/DeploymentDetailPage';
import { AnalyticsPage } from '@/app/analytics/AnalyticsPage';
import { PromotionsPage } from '@/app/promotions/PromotionsPage';
import { PromotionDetailPage } from '@/app/promotions/PromotionDetailPage';
import { RollbacksPage } from '@/app/rollbacks/RollbacksPage';
import { MyQueuePage } from '@/app/me/MyQueuePage';
import { MyTasksPage } from '@/app/me/MyTasksPage';
import { WorkItemDetailPage } from '@/app/work-items/WorkItemDetailPage';
import { SettingsPage } from '@/app/settings/SettingsPage';
import { EnvironmentsSettings } from '@/app/settings/EnvironmentsSettings';
import { RolesSettings } from '@/app/settings/RolesSettings';
import { ActivityTemplateSettings } from '@/app/settings/ActivityTemplateSettings';
import { FeatureFlagSettings } from '@/app/settings/FeatureFlagSettings';
import { CatalogSettings } from '@/app/settings/CatalogSettings';
import { PromotionSettings } from '@/app/settings/PromotionSettings';
import { RollbackSettings } from '@/app/settings/RollbackSettings';
import { MaintenanceSettings } from '@/app/settings/MaintenanceSettings';
import { ReleaseNoteTemplateSettings } from '@/app/settings/ReleaseNoteTemplateSettings';
import { ReleaseNotesPage } from '@/app/release-notes/ReleaseNotesPage';
import { ReleaseNotesIndexPage } from '@/app/release-notes/ReleaseNotesIndexPage';
import { ReleaseNoteDraftPage } from '@/app/release-notes/ReleaseNoteDraftPage';
import { ReleaseNoteDetailPage } from '@/app/release-notes/ReleaseNoteDetailPage';
import { WebhookListPage } from '@/app/webhooks/WebhookListPage';
import { WebhookDetailPage } from '@/app/webhooks/WebhookDetailPage';
import { AdminRoute } from '@/components/auth/AdminRoute';
import { FeatureRoute } from '@/components/auth/FeatureRoute';
import { FeatureFlag } from '@/stores/featureFlagsStore';

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route element={<Layout />}>
          <Route path="/" element={<Navigate to="/catalog" replace />} />
          {/* "My tasks" — the topbar bell's destination: everything awaiting the signed-in user,
              across promotions and work items. Not feature-gated: it degrades to an empty page
              when Promotions is off, and it's the target of a permanent shell affordance. */}
          <Route path="/my-tasks" element={<MyTasksPage />} />
          <Route path="/catalog" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><CatalogPage /></FeatureRoute>} />
          <Route path="/catalog/:slug" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><RequestPage /></FeatureRoute>} />
          <Route path="/requests" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><RequestsPage /></FeatureRoute>} />
          <Route path="/requests/:id" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><RequestDetailPage /></FeatureRoute>} />
          <Route path="/approvals" element={<FeatureRoute flag={FeatureFlag.Approvals}><ApprovalsPage /></FeatureRoute>} />
          <Route path="/approvals/:id" element={<FeatureRoute flag={FeatureFlag.Approvals}><ApprovalDetailPage /></FeatureRoute>} />
          <Route path="/deployments" element={<DeploymentsPage />} />
          {/* Deploy-event detail. Keyed on the event id alone — product and service are properties of
              the event, not of its identity — and ranked above /deployments/:product by the router's
              static-segment-wins rule, so "events" can't be read as a product name. */}
          <Route path="/deployments/events/:id" element={<DeploymentDetailPage />} />
          <Route path="/deployments/:product" element={<ProductDeploymentsPage />} />
          <Route path="/deployments/:product/:service/history" element={<DeploymentHistoryPage />} />
          <Route path="/analytics" element={<FeatureRoute flag={FeatureFlag.Analytics}><AnalyticsPage /></FeatureRoute>} />
          <Route path="/promotions" element={<FeatureRoute flag={FeatureFlag.Promotions}><PromotionsPage /></FeatureRoute>} />
          <Route path="/promotions/:id" element={<FeatureRoute flag={FeatureFlag.Promotions}><PromotionDetailPage /></FeatureRoute>} />
          {/* "My queue" — work items awaiting the current user's signoff across products/envs. */}
          <Route path="/me/work-items" element={<FeatureRoute flag={FeatureFlag.Promotions}><MyQueuePage /></FeatureRoute>} />
          {/* Work-item detail. The sign-off grain is (key, product, targetEnv), so product and
              targetEnv ride along as query params rather than as extra path segments. */}
          <Route path="/work-items/:key" element={<FeatureRoute flag={FeatureFlag.Promotions}><WorkItemDetailPage /></FeatureRoute>} />
          <Route path="/rollbacks" element={<FeatureRoute flag={FeatureFlag.Rollbacks}><RollbacksPage /></FeatureRoute>} />
          <Route path="/release-notes" element={<FeatureRoute flag={FeatureFlag.ReleaseNotes}><ReleaseNotesIndexPage /></FeatureRoute>} />
          <Route path="/release-notes/:product" element={<FeatureRoute flag={FeatureFlag.ReleaseNotes}><ReleaseNotesPage /></FeatureRoute>} />
          {/* "new" route must come before the dynamic :id route so it isn't captured as an id. */}
          <Route path="/release-notes/:product/new" element={<FeatureRoute flag={FeatureFlag.ReleaseNotes}><ReleaseNoteDraftPage /></FeatureRoute>} />
          <Route path="/release-notes/:product/:id" element={<FeatureRoute flag={FeatureFlag.ReleaseNotes}><ReleaseNoteDetailPage /></FeatureRoute>} />
          <Route path="/webhooks" element={<AdminRoute><WebhookListPage /></AdminRoute>} />
          <Route path="/webhooks/:id" element={<AdminRoute><WebhookDetailPage /></AdminRoute>} />
          <Route path="/settings" element={<AdminRoute><SettingsPage /></AdminRoute>}>
            <Route index element={<Navigate to="environments" replace />} />
            <Route path="environments" element={<EnvironmentsSettings />} />
            <Route path="roles" element={<RolesSettings />} />
            <Route path="activity-template" element={<ActivityTemplateSettings />} />
            <Route path="feature-flags" element={<FeatureFlagSettings />} />
            <Route path="catalog" element={<CatalogSettings />} />
            <Route path="promotions" element={<PromotionSettings />} />
            <Route path="rollbacks" element={<RollbackSettings />} />
            <Route path="maintenance" element={<MaintenanceSettings />} />
            {/* The old name, kept as a redirect — this page is where bookmarked one-off fixes live,
                which is exactly the kind of page that gets bookmarked. */}
            <Route path="deployment-maintenance" element={<Navigate to="../maintenance" replace />} />
            <Route path="release-notes-template" element={<ReleaseNoteTemplateSettings />} />
          </Route>
        </Route>
      </Routes>
    </BrowserRouter>
  );
}

export default App;
