import { create } from 'zustand';
import { persist } from 'zustand/middleware';

interface AuthState {
  username: string | null;
  password: string | null;
  isAuthenticated: boolean;
  login: (username: string, password: string) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      username: null,
      password: null,
      isAuthenticated: false,
      login: (username, password) => {
        set({ username, password, isAuthenticated: true });
      },
      logout: () => {
        set({ username: null, password: null, isAuthenticated: false });
      },
    }),
    {
      name: 'evertask-auth',
    }
  )
);
