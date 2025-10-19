import { Home, List, Layers, Activity, BarChart3, Menu, X } from 'lucide-react';
import { Link, useLocation } from 'react-router-dom';
import { cn } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import { useState } from 'react';

const navigation = [
  { name: 'Overview', href: '/', icon: Home },
  { name: 'Tasks', href: '/tasks', icon: List },
  { name: 'Queues', href: '/queues', icon: Layers },
  { name: 'Live Monitoring', href: '/live', icon: Activity },
  { name: 'Statistics', href: '/statistics', icon: BarChart3 },
];

export function Sidebar() {
  const location = useLocation();
  const [isMobileOpen, setIsMobileOpen] = useState(false);

  const isActive = (href: string) => {
    if (href === '/') {
      return location.pathname === '/';
    }
    return location.pathname.startsWith(href);
  };

  const NavContent = () => (
    <nav className="space-y-1 px-2">
      {navigation.map((item) => {
        const active = isActive(item.href);
        return (
          <Link
            key={item.name}
            to={item.href}
            onClick={() => setIsMobileOpen(false)}
            className={cn(
              'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
              active
                ? 'bg-blue-50 text-blue-700'
                : 'text-gray-700 hover:bg-gray-100 hover:text-gray-900'
            )}
          >
            <item.icon className="h-5 w-5" />
            <span>{item.name}</span>
          </Link>
        );
      })}
    </nav>
  );

  return (
    <>
      {/* Mobile menu button */}
      <div className="lg:hidden fixed top-4 left-4 z-40">
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setIsMobileOpen(!isMobileOpen)}
          className="bg-white border shadow-sm"
        >
          {isMobileOpen ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
        </Button>
      </div>

      {/* Mobile sidebar */}
      {isMobileOpen && (
        <>
          <div
            className="fixed inset-0 bg-black/50 z-40 lg:hidden"
            onClick={() => setIsMobileOpen(false)}
          />
          <aside className="fixed top-0 left-0 z-50 w-64 h-full bg-white border-r shadow-lg lg:hidden">
            <div className="flex h-16 items-center px-6 border-b">
              <span className="font-semibold text-lg">Menu</span>
            </div>
            <div className="py-4">
              <NavContent />
            </div>
          </aside>
        </>
      )}

      {/* Desktop sidebar - fixed position with full height */}
      <aside className="hidden lg:block fixed left-0 top-16 w-64 h-[calc(100vh-4rem)] border-r bg-white overflow-y-auto">
        <div className="py-4">
          <NavContent />
        </div>
      </aside>
    </>
  );
}
