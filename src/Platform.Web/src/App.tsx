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
import { PromotionsPage } from '@/app/promotions/PromotionsPage';
import { PromotionDetailPage } from '@/app/promotions/PromotionDetailPage';
import { MyQueuePage } from '@/app/me/MyQueuePage';
import { SettingsPage } from '@/app/settings/SettingsPage';
import { EnvironmentsSettings } from '@/app/settings/EnvironmentsSettings';
import { RolesSettings } from '@/app/settings/RolesSettings';
import { ActivityTemplateSettings } from '@/app/settings/ActivityTemplateSettings';
import { FeatureFlagSettings } from '@/app/settings/FeatureFlagSettings';
import { CatalogSettings } from '@/app/settings/CatalogSettings';
import { PromotionSettings } from '@/app/settings/PromotionSettings';
import { DeploymentMaintenanceSettings } from '@/app/settings/DeploymentMaintenanceSettings';
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
          <Route path="/catalog" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><CatalogPage /></FeatureRoute>} />
          <Route path="/catalog/:slug" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><RequestPage /></FeatureRoute>} />
          <Route path="/requests" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><RequestsPage /></FeatureRoute>} />
          <Route path="/requests/:id" element={<FeatureRoute flag={FeatureFlag.ServiceCatalog}><RequestDetailPage /></FeatureRoute>} />
          <Route path="/approvals" element={<FeatureRoute flag={FeatureFlag.Approvals}><ApprovalsPage /></FeatureRoute>} />
          <Route path="/approvals/:id" element={<FeatureRoute flag={FeatureFlag.Approvals}><ApprovalDetailPage /></FeatureRoute>} />
          <Route path="/deployments" element={<DeploymentsPage />} />
          <Route path="/deployments/:product" element={<ProductDeploymentsPage />} />
          <Route path="/deployments/:product/:service/history" element={<DeploymentHistoryPage />} />
          <Route path="/promotions" element={<FeatureRoute flag={FeatureFlag.Promotions}><PromotionsPage /></FeatureRoute>} />
          <Route path="/promotions/:id" element={<FeatureRoute flag={FeatureFlag.Promotions}><PromotionDetailPage /></FeatureRoute>} />
          {/* "My queue" — tickets awaiting the current user's signoff across products/envs. */}
          <Route path="/me/tickets" element={<FeatureRoute flag={FeatureFlag.Promotions}><MyQueuePage /></FeatureRoute>} />
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
            <Route path="deployment-maintenance" element={<DeploymentMaintenanceSettings />} />
            <Route path="release-notes-template" element={<ReleaseNoteTemplateSettings />} />
          </Route>
        </Route>
      </Routes>
    </BrowserRouter>
  );
}

export default App;
