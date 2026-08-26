import { Navigate } from 'react-router-dom';
import { useFeatureFlagsStore, type FeatureFlagKey } from '@/stores/featureFlagsStore';

export function FeatureRoute({ flag, children }: { flag: FeatureFlagKey; children: React.ReactNode }) {
  const loaded = useFeatureFlagsStore((s) => s.loaded);
  const enabled = useFeatureFlagsStore((s) => s.isEnabled(flag));

  // Wait until the initial fetch lands so we don't bounce a user off their deep-link
  // during the tiny window between app boot and /api/features responding.
  if (!loaded) return null;

  if (!enabled) return <Navigate to="/deployments" replace />;

  return <>{children}</>;
}
