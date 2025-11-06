import { create } from 'zustand';
import { persist } from 'zustand/middleware';

interface AuthState {
  token: string | null;
  username: string | null;
  expiresAt: string | null;
  isAuthenticated: boolean;
  setAuthData: (token: string, username: string, expiresAt: string) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      token: null,
      username: null,
      expiresAt: null,
      isAuthenticated: false,
      setAuthData: (token, username, expiresAt) => {
        set({ token, username, expiresAt, isAuthenticated: true });
      },
      logout: () => {
        set({ token: null, username: null, expiresAt: null, isAuthenticated: false });
      },
    }),
    {
      name: 'evertask-auth',
    }
  )
);
