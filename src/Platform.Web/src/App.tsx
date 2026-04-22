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
import { SettingsPage } from '@/app/settings/SettingsPage';
import { EnvironmentsSettings } from '@/app/settings/EnvironmentsSettings';
import { RolesSettings } from '@/app/settings/RolesSettings';
import { ActivityTemplateSettings } from '@/app/settings/ActivityTemplateSettings';
import { FeatureFlagSettings } from '@/app/settings/FeatureFlagSettings';
import { CatalogSettings } from '@/app/settings/CatalogSettings';
import { PromotionSettings } from '@/app/settings/PromotionSettings';
import { DeploymentMaintenanceSettings } from '@/app/settings/DeploymentMaintenanceSettings';
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
          </Route>
        </Route>
      </Routes>
    </BrowserRouter>
  );
}

export default App;
