import { LogOut, User } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Badge } from '@/components/ui/badge';
import { useAuthStore } from '@/stores/authStore';
import { useRealtimeStore } from '@/stores/realtimeStore';
import { useNavigate } from 'react-router-dom';
import logoIcon from '@/assets/logo-icon.png';
import logoFull from '@/assets/logo-full.png';

export function Header() {
  const { username, logout } = useAuthStore();
  const connectionStatus = useRealtimeStore((state) => state.connectionStatus);
  const navigate = useNavigate();

  const handleLogout = () => {
    logout();
    navigate('/login');
  };

  const getConnectionBadge = () => {
    switch (connectionStatus) {
      case 'connected':
        return (
          <Badge variant="outline" className="bg-green-50 text-green-700 border-green-200">
            <div className="w-2 h-2 bg-green-500 rounded-full mr-2 animate-pulse" />
            Connected
          </Badge>
        );
      case 'reconnecting':
        return (
          <Badge variant="outline" className="bg-yellow-50 text-yellow-700 border-yellow-200">
            <div className="w-2 h-2 bg-yellow-500 rounded-full mr-2 animate-pulse" />
            Reconnecting
          </Badge>
        );
      case 'disconnected':
        return (
          <Badge variant="outline" className="bg-red-50 text-red-700 border-red-200">
            <div className="w-2 h-2 bg-red-500 rounded-full mr-2" />
            Disconnected
          </Badge>
        );
    }
  };

  return (
    <header className="border-b bg-white sticky top-0 z-50">
      <div className="flex h-16 items-center px-4 md:px-6">
        <div className="flex items-center gap-2">
          {/* Logo icon for mobile, full logo for desktop */}
          <img
            src={logoIcon}
            alt="EverTask"
            className="h-8 w-8 md:hidden"
          />
          <img
            src={logoFull}
            alt="EverTask Monitor"
            className="hidden md:block h-8"
          />
          <span className="hidden sm:inline md:hidden font-semibold text-lg">Monitor</span>
        </div>

        <div className="ml-auto flex items-center gap-4">
          {getConnectionBadge()}

          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" className="rounded-full">
                <User className="w-5 h-5" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              <DropdownMenuLabel>
                <div className="flex flex-col space-y-1">
                  <p className="text-sm font-medium">Logged in as</p>
                  <p className="text-xs text-muted-foreground">{username || 'User'}</p>
                </div>
              </DropdownMenuLabel>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={handleLogout} className="text-red-600 cursor-pointer">
                <LogOut className="mr-2 h-4 w-4" />
                <span>Log out</span>
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>
    </header>
  );
}
