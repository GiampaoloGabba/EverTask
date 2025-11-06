import { useAuthStore } from '@/stores/authStore';
import { useNavigate } from 'react-router-dom';

export const useAuth = () => {
  const navigate = useNavigate();
  const { isAuthenticated, username, logout: logoutStore } = useAuthStore();

  const logout = () => {
    logoutStore();
    navigate('/login');
  };

  return {
    isAuthenticated,
    username,
    logout,
  };
};
