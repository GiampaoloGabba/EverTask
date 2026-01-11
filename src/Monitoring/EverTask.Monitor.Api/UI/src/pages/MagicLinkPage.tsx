import { useEffect, useState } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { useAuthStore } from '@/stores/authStore';
import { apiService } from '@/services/api';

export function MagicLinkPage() {
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const setAuthData = useAuthStore((state) => state.setAuthData);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const token = searchParams.get('token');
    if (!token) {
      setError('Missing token parameter');
      setLoading(false);
      return;
    }

    apiService.magicLinkLogin(token)
      .then((response) => {
        const { token: jwtToken, username, expiresAt } = response.data;
        setAuthData(jwtToken, username, expiresAt);
        navigate('/', { replace: true });
      })
      .catch((err) => {
        const message = err.response?.data?.message || 'Invalid or expired magic link';
        setError(message);
        setLoading(false);
      });
  }, [searchParams, setAuthData, navigate]);

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background">
        <div className="text-center">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary mx-auto"></div>
          <p className="mt-4 text-muted-foreground">Authenticating...</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-background">
        <div className="text-center space-y-4">
          <div className="text-destructive text-lg">{error}</div>
          <a
            href="/evertask-monitoring/login"
            className="text-primary hover:underline block"
          >
            Go to login page
          </a>
        </div>
      </div>
    );
  }

  return null;
}
