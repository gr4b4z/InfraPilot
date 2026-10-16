import { Navigate } from 'react-router-dom';
import { useAuthStore } from '@/stores/authStore';

/**
 * Pages for the people who *act* on tasks — QA and Admins. Everyone else is sent to
 * Deployments, which is also the plain user's landing page, rather than to `/` (whose
 * redirect depends on the same role check and could loop).
 */
export function QaRoute({ children }: { children: React.ReactNode }) {
  const user = useAuthStore((s) => s.user);
  const isLoading = useAuthStore((s) => s.isLoading);

  if (isLoading) return null;

  if (!user?.isQA && !user?.isAdmin) {
    return <Navigate to="/deployments" replace />;
  }

  return <>{children}</>;
}
