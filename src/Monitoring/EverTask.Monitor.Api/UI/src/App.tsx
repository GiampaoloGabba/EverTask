import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router-dom';
import { useEffect, useState } from 'react';
import { configService } from './services/config';
import { useConfigStore } from './stores/configStore';
import { signalRService } from './services/signalr';
import { useAuthStore } from './stores/authStore';
import router from './router';
import { Toaster } from '@/components/ui/toaster';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30000, // 30 seconds
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});

function App() {
  const [isConfigLoaded, setIsConfigLoaded] = useState(false);
  const setConfig = useConfigStore((state) => state.setConfig);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);

  useEffect(() => {
    // Fetch configuration on app startup
    configService
      .fetchConfig()
      .then((config) => {
        setConfig(config);
        setIsConfigLoaded(true);
      })
      .catch((error) => {
        console.error('Failed to load configuration:', error);
        // Still mark as loaded to show the app (user can try to login)
        setIsConfigLoaded(true);
      });
  }, [setConfig]);

  // Initialize SignalR connection when authenticated
  useEffect(() => {
    if (isAuthenticated && isConfigLoaded) {
      signalRService.start().catch((error: unknown) => {
        console.error('Failed to start SignalR connection:', error);
      });
    }

    return () => {
      if (isAuthenticated) {
        signalRService.stop().catch((error: unknown) => {
          console.error('Failed to stop SignalR connection:', error);
        });
      }
    };
  }, [isAuthenticated, isConfigLoaded]);

  if (!isConfigLoaded) {
    return (
      <div className="flex items-center justify-center min-h-screen bg-gray-50">
        <LoadingSpinner text="Loading configuration..." size="lg" />
      </div>
    );
  }

  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
      <Toaster />
    </QueryClientProvider>
  );
}

export default App;
