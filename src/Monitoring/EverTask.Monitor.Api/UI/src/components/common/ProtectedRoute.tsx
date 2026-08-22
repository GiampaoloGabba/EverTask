import { Navigate } from 'react-router-dom';
import { useAuthStore } from '@/stores/authStore';

// Protected route wrapper
export function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }

  return <>{children}</>;
}
