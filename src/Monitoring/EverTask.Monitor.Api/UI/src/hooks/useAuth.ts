import { useAuthStore } from '@/stores/authStore';
import { useNavigate } from 'react-router-dom';

export const useAuth = () => {
  const navigate = useNavigate();
  const { isAuthenticated, username, login: loginStore, logout: logoutStore } = useAuthStore();

  const login = (username: string, password: string) => {
    loginStore(username, password);
    navigate('/');
  };

  const logout = () => {
    logoutStore();
    navigate('/login');
  };

  return {
    isAuthenticated,
    username,
    login,
    logout,
  };
};
