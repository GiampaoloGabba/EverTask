import { RefreshCw } from 'lucide-react';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  DropdownMenuSeparator,
} from '@/components/ui/dropdown-menu';
import { Badge } from '@/components/ui/badge';
import { useRefreshStore, RefreshMode } from '@/stores/refreshStore';
import { useSignalRRefresh } from '@/hooks/useSignalRRefresh';

interface RefreshOption {
  value: string;
  label: string;
  mode: RefreshMode;
  interval?: number;
}

const REFRESH_OPTIONS: RefreshOption[] = [
  { value: 'signalr', label: 'SignalR (Real-time)', mode: 'signalr' },
  { value: 'polling-5', label: 'Polling: 5 seconds', mode: 'polling', interval: 5000 },
  { value: 'polling-10', label: 'Polling: 10 seconds', mode: 'polling', interval: 10000 },
  { value: 'polling-30', label: 'Polling: 30 seconds', mode: 'polling', interval: 30000 },
  { value: 'polling-60', label: 'Polling: 1 minute', mode: 'polling', interval: 60000 },
  { value: 'disabled', label: 'Disabled', mode: 'disabled' },
];

export function RefreshControl() {
  const { mode, pollingInterval, setMode, setPollingInterval } = useRefreshStore();
  const { isSignalRActive, connectionStatus, isRefreshing } = useSignalRRefresh();

  // Determine current selection value
  const currentValue = mode === 'signalr'
    ? 'signalr'
    : mode === 'disabled'
    ? 'disabled'
    : `polling-${pollingInterval ? pollingInterval / 1000 : 10}`;

  const handleChange = (value: string) => {
    const option = REFRESH_OPTIONS.find((opt) => opt.value === value);
    if (!option) return;

    setMode(option.mode);
    if (option.interval !== undefined) {
      setPollingInterval(option.interval);
    }
  };

  // Determine refresh mode badge
  const getRefreshModeBadge = () => {
    if (mode === 'disabled') {
      return (
        <Badge variant="outline" className="bg-gray-50 text-gray-700 border-gray-200">
          <RefreshCw className="w-3 h-3 mr-1.5" />
          Manual only
        </Badge>
      );
    }

    if (mode === 'signalr') {
      if (isSignalRActive) {
        return (
          <Badge variant="outline" className="bg-green-50 text-green-700 border-green-200">
            <RefreshCw className={`w-3 h-3 mr-1.5 ${isRefreshing ? 'animate-spin' : ''}`} />
            Real-time
          </Badge>
        );
      } else {
        // SignalR mode but not connected - show fallback status
        if (connectionStatus === 'reconnecting') {
          return (
            <Badge variant="outline" className="bg-yellow-50 text-yellow-700 border-yellow-200">
              <RefreshCw className="w-3 h-3 mr-1.5 animate-spin" />
              Connecting...
            </Badge>
          );
        } else {
          // Disconnected - show fallback to polling
          return (
            <Badge variant="outline" className="bg-orange-50 text-orange-700 border-orange-200">
              <RefreshCw className="w-3 h-3 mr-1.5" />
              Polling (fallback)
            </Badge>
          );
        }
      }
    }

    // Polling mode
    const intervalSeconds = pollingInterval ? pollingInterval / 1000 : 10;
    return (
      <Badge variant="outline" className="bg-blue-50 text-blue-700 border-blue-200">
        <RefreshCw className="w-3 h-3 mr-1.5" />
        Polling ({intervalSeconds}s)
      </Badge>
    );
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button className="cursor-pointer focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 rounded-md transition-all hover:opacity-80">
          {getRefreshModeBadge()}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-48">
        {/* SignalR Option */}
        <DropdownMenuItem
          onClick={() => handleChange('signalr')}
          className={currentValue === 'signalr' ? 'bg-blue-50' : ''}
        >
          <RefreshCw className="w-4 h-4 mr-2" />
          <span>SignalR (Real-time)</span>
        </DropdownMenuItem>

        <DropdownMenuSeparator />

        {/* Polling Options */}
        <DropdownMenuItem
          onClick={() => handleChange('polling-5')}
          className={currentValue === 'polling-5' ? 'bg-blue-50' : ''}
        >
          <RefreshCw className="w-4 h-4 mr-2" />
          <span>Polling: 5 seconds</span>
        </DropdownMenuItem>
        <DropdownMenuItem
          onClick={() => handleChange('polling-10')}
          className={currentValue === 'polling-10' ? 'bg-blue-50' : ''}
        >
          <RefreshCw className="w-4 h-4 mr-2" />
          <span>Polling: 10 seconds</span>
        </DropdownMenuItem>
        <DropdownMenuItem
          onClick={() => handleChange('polling-30')}
          className={currentValue === 'polling-30' ? 'bg-blue-50' : ''}
        >
          <RefreshCw className="w-4 h-4 mr-2" />
          <span>Polling: 30 seconds</span>
        </DropdownMenuItem>
        <DropdownMenuItem
          onClick={() => handleChange('polling-60')}
          className={currentValue === 'polling-60' ? 'bg-blue-50' : ''}
        >
          <RefreshCw className="w-4 h-4 mr-2" />
          <span>Polling: 1 minute</span>
        </DropdownMenuItem>

        <DropdownMenuSeparator />

        {/* Disabled Option */}
        <DropdownMenuItem
          onClick={() => handleChange('disabled')}
          className={currentValue === 'disabled' ? 'bg-blue-50' : ''}
        >
          <RefreshCw className="w-4 h-4 mr-2" />
          <span>Disabled</span>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
