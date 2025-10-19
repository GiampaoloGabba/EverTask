# CLAUDE.md - EverTask Monitor Dashboard UI

React TypeScript SPA for monitoring EverTask background tasks. Embedded in `EverTask.Monitor.Api` NuGet package.

## Build Commands

```bash
# Install dependencies
npm install

# Development server (with HMR)
npm run dev
# → http://localhost:5173
# → Proxies API calls to http://localhost:5000

# Production build
npm run build
# → Outputs to ../wwwroot/ (embedded in .NET package)

# Type checking
npm run type-check

# Linting
npm run lint
```

## Tech Stack

- **React 18** + **TypeScript 5**
- **Vite 5** (build tool)
- **Tailwind CSS 3** + **shadcn/ui** (components)
- **TanStack Query 5** (server state)
- **Zustand 4** (client state)
- **React Router 6** (routing)
- **Recharts 2** (charts)
- **@microsoft/signalr 8** (real-time)
- **date-fns 3** (dates)
- **Axios 1** (HTTP)
- **lucide-react** (icons)
- **react-syntax-highlighter** (JSON viewer)

## Project Structure

```
src/
├── components/
│   ├── common/           # Reusable components (TaskStatusBadge, JsonViewer, Timeline, etc.)
│   ├── dashboard/        # Dashboard-specific (KPICard, Charts, ActivityFeed)
│   ├── layout/           # Layout (Header, Sidebar, MainLayout)
│   ├── queues/           # Queue components (QueueCard, QueueComparisonChart)
│   ├── statistics/       # Statistics charts
│   ├── tasks/            # Task components (TasksTable, TaskFilters, TaskDetailModal)
│   └── ui/               # shadcn/ui components (auto-generated)
├── pages/                # Page components (OverviewPage, TasksPage, etc.)
├── hooks/                # React Query hooks (useTasks, useDashboard, useQueues, etc.)
├── stores/               # Zustand stores (auth, realtime, config)
├── services/             # API services (api.ts, signalr.ts, config.ts)
├── types/                # TypeScript types (match backend DTOs exactly)
├── utils/                # Utilities (date formatting, status helpers, etc.)
├── styles/               # Global styles (globals.css)
├── lib/                  # Third-party lib configs (utils.ts for shadcn)
├── router.tsx            # React Router config
├── App.tsx               # Main app component
└── main.tsx              # Entry point
```

## Key Concepts

### Type System (Match Backend DTOs)

TypeScript types in `src/types/*.ts` MUST match backend C# DTOs exactly (camelCase JSON):

```typescript
// src/types/task.types.ts
export interface TaskListDto {
  id: string;                        // Guid in C#
  type: string;
  status: QueuedTaskStatus;
  queueName: string | null;
  createdAtUtc: string;              // DateTimeOffset in C#
  lastExecutionUtc: string | null;
  scheduledExecutionUtc: string | null;
  isRecurring: boolean;
  recurringInfo: string | null;
  currentRunCount: number | null;
  maxRuns: number | null;
}
```

### API Service (src/services/api.ts)

- Axios-based client
- Auto-initializes from `/api/config` endpoint
- Adds Basic Auth header to all requests
- Handles 401 → logout & redirect
- All methods return `AxiosResponse<T>`

```typescript
// Usage in hooks
const response = await apiService.getTasks(filter, pagination);
return response.data; // TasksPagedResponse
```

### Zustand Stores (src/stores/)

**authStore**: Authentication state (persisted to localStorage)
```typescript
const { username, password, isAuthenticated, login, logout } = useAuthStore();
```

**realtimeStore**: SignalR events (max 200 events)
```typescript
const { events, connectionStatus, isPaused, addEvent, togglePause } = useRealtimeStore();
```

**configStore**: Runtime config from backend
```typescript
const { config, setConfig } = useConfigStore();
```

### React Query Hooks (src/hooks/)

All hooks use `@tanstack/react-query` for server state:

```typescript
// src/hooks/useTasks.ts
export const useTasks = (filter: TaskFilter, pagination: PaginationParams) => {
  return useQuery({
    queryKey: ['tasks', filter, pagination],
    queryFn: async () => {
      const response = await apiService.getTasks(filter, pagination);
      return response.data;
    },
  });
};

// Usage in components
const { data, isLoading, error } = useTasks(filter, pagination);
```

### SignalR Integration (src/services/signalr.ts)

Auto-connects when authenticated. Events added to `realtimeStore`:

```typescript
// In App.tsx
useEffect(() => {
  if (isAuthenticated) {
    signalRService.initialize();
  }
  return () => signalRService.disconnect();
}, [isAuthenticated]);
```

### Routing (src/router.tsx)

React Router v6 with protected routes:

```typescript
const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  {
    path: '/',
    element: <ProtectedRoute><MainLayout /></ProtectedRoute>,
    children: [
      { index: true, element: <OverviewPage /> },
      { path: 'tasks', element: <TasksPage /> },
      { path: 'tasks/:id', element: <TaskDetailPage /> },
      // ...
    ],
  },
], { basename: '/evertask' }); // Match Vite base
```

## shadcn/ui Components

Install components as needed:

```bash
npx shadcn@latest add button
npx shadcn@latest add card
npx shadcn@latest add table
# etc.
```

Installed components go to `src/components/ui/`. Import and use:

```typescript
import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardContent } from '@/components/ui/card';
```

## Creating New Components

### Common Component Example

```typescript
// src/components/common/MyComponent.tsx
import React from 'react';
import { Card } from '@/components/ui/card';

interface MyComponentProps {
  title: string;
  children?: React.ReactNode;
}

export function MyComponent({ title, children }: MyComponentProps) {
  return (
    <Card>
      <h2 className="text-lg font-semibold">{title}</h2>
      {children}
    </Card>
  );
}
```

### Page Component Example

```typescript
// src/pages/MyPage.tsx
import React from 'react';
import { useSomeData } from '@/hooks/useSomeData';
import { LoadingSpinner } from '@/components/common/LoadingSpinner';
import { EmptyState } from '@/components/common/EmptyState';

export function MyPage() {
  const { data, isLoading } = useSomeData();

  if (isLoading) return <LoadingSpinner />;
  if (!data?.items.length) return <EmptyState title="No data" description="..." />;

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">My Page</h1>
      {/* Content */}
    </div>
  );
}
```

### React Query Hook Example

```typescript
// src/hooks/useMyData.ts
import { useQuery } from '@tanstack/react-query';
import { apiService } from '@/services/api';

export const useMyData = (filter: MyFilter) => {
  return useQuery({
    queryKey: ['myData', filter],
    queryFn: async () => {
      const response = await apiService.getMyData(filter);
      return response.data;
    },
    refetchInterval: 30000, // Optional: auto-refresh every 30s
  });
};
```

## Styling Guidelines

### Tailwind Utilities

Use Tailwind utility classes:

```tsx
<div className="flex items-center justify-between p-4 bg-white rounded-lg shadow">
  <h2 className="text-xl font-bold text-gray-900">Title</h2>
  <span className="text-sm text-gray-500">Subtitle</span>
</div>
```

### Responsive Design

Mobile-first breakpoints:

```tsx
<div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
  {/* 1 col mobile, 2 cols tablet, 4 cols desktop */}
</div>
```

### Status Colors (Consistent Across UI)

```typescript
// Use in TaskStatusBadge and charts
WaitingQueue: 'bg-blue-500'
Queued: 'bg-blue-500'
InProgress: 'bg-yellow-500'
Pending: 'bg-purple-500'
Completed: 'bg-green-500'
Failed: 'bg-red-500'
Cancelled: 'bg-gray-500'
ServiceStopped: 'bg-gray-500'
```

## Data Handling Patterns

### Recurring Tasks

Check `isRecurring` field and show additional UI:

```tsx
{task.isRecurring && (
  <>
    <Badge>Recurring</Badge>
    <p className="text-sm text-gray-600">{task.recurringInfo}</p>
    {task.maxRuns && (
      <p className="text-sm">
        {task.currentRunCount} / {task.maxRuns} runs
      </p>
    )}
    {task.nextRunUtc && (
      <p className="text-sm">Next: {formatDate(task.nextRunUtc)}</p>
    )}
  </>
)}
```

### JSON Parsing (Request Field)

```typescript
// Safe JSON parse with fallback
try {
  const parsed = JSON.parse(task.request);
  return <JsonViewer data={parsed} />;
} catch {
  return <pre className="text-xs">{task.request}</pre>;
}
```

### Handler Name Formatting

```typescript
// Extract short name from assembly-qualified name
function getShortHandlerName(fullName: string): string {
  const parts = fullName.split(',')[0].split('.');
  return parts[parts.length - 1]; // Last part
}

// "Kv.Workers.Emails.ContactFormEmailTaskHandler, ..." → "ContactFormEmailTaskHandler"
```

### Date Formatting

```typescript
import { formatDistanceToNow, format } from 'date-fns';

// Relative: "2 hours ago"
formatDistanceToNow(new Date(task.createdAtUtc), { addSuffix: true });

// Absolute: "Jan 15, 2025 14:30"
format(new Date(task.createdAtUtc), 'MMM dd, yyyy HH:mm');
```

## Charts (Recharts)

### Line Chart Example

```typescript
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer } from 'recharts';

<ResponsiveContainer width="100%" height={300}>
  <LineChart data={data}>
    <CartesianGrid strokeDasharray="3 3" />
    <XAxis dataKey="timestamp" />
    <YAxis />
    <Tooltip />
    <Legend />
    <Line type="monotone" dataKey="completed" stroke="#10b981" strokeWidth={2} />
    <Line type="monotone" dataKey="failed" stroke="#ef4444" strokeWidth={2} />
  </LineChart>
</ResponsiveContainer>
```

### Pie Chart Example

```typescript
import { PieChart, Pie, Cell, Tooltip, Legend, ResponsiveContainer } from 'recharts';

const COLORS = ['#3b82f6', '#eab308', '#8b5cf6', '#10b981', '#ef4444', '#6b7280'];

<ResponsiveContainer width="100%" height={300}>
  <PieChart>
    <Pie data={data} dataKey="value" nameKey="name" cx="50%" cy="50%" outerRadius={80} label>
      {data.map((entry, index) => (
        <Cell key={`cell-${index}`} fill={COLORS[index % COLORS.length]} />
      ))}
    </Pie>
    <Tooltip />
    <Legend />
  </PieChart>
</ResponsiveContainer>
```

## Common Patterns

### Loading States

```tsx
import { Skeleton } from '@/components/ui/skeleton';

{isLoading ? (
  <div className="space-y-2">
    <Skeleton className="h-4 w-full" />
    <Skeleton className="h-4 w-3/4" />
  </div>
) : (
  <div>{/* Content */}</div>
)}
```

### Empty States

```tsx
import { EmptyState } from '@/components/common/EmptyState';
import { FileQuestion } from 'lucide-react';

{!data?.items.length && (
  <EmptyState
    icon={FileQuestion}
    title="No tasks found"
    description="Try adjusting your filters"
  />
)}
```

### Error Handling

```tsx
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { AlertCircle } from 'lucide-react';

{error && (
  <Alert variant="destructive">
    <AlertCircle className="h-4 w-4" />
    <AlertTitle>Error</AlertTitle>
    <AlertDescription>{error.message}</AlertDescription>
  </Alert>
)}
```

## Integration with Backend

### App Initialization Flow

1. App loads, renders `<App />`
2. `useEffect` in App.tsx fetches `/api/config`
3. Config stored in `configStore`
4. `ApiService` initializes with `apiBasePath`
5. `SignalRService` initializes with `signalRHubPath` (if authenticated)
6. User logs in → `authStore.login()` → redirects to `/`
7. Protected routes render, components fetch data via React Query

### API Endpoints Used

All relative to `apiBasePath` (from config):

- `GET /tasks` - Task list
- `GET /tasks/:id` - Task details
- `GET /tasks/:id/status-audit` - Status history
- `GET /tasks/:id/runs-audit` - Runs history
- `GET /dashboard/overview` - Overview stats
- `GET /dashboard/recent-activity` - Activity feed
- `GET /queues` - Queue metrics
- `GET /queues/:name/tasks` - Queue tasks
- `GET /statistics/success-rate-trend` - Success rate trend
- `GET /statistics/task-types` - Task type distribution
- `GET /statistics/execution-times` - Execution times
- `GET /config` - Runtime config (no auth)

### SignalR Hub

- Path: `signalRHubPath` (from config, default `/evertask/monitor`)
- Event: `EverTaskEvent` → `EverTaskEventData`
- Auto-reconnects on disconnection

## Vite Configuration

**vite.config.ts** key settings:

```typescript
export default defineConfig({
  base: '/evertask/',              // Match backend BasePath
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  build: {
    outDir: '../wwwroot',          // Build to parent wwwroot (embedded in .NET)
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/evertask/api': { target: 'http://localhost:5000' },
      '/evertask/monitor': { target: 'http://localhost:5000', ws: true },
    },
  },
});
```

## Build Output

Production build creates:

```
../wwwroot/
├── index.html
└── assets/
    ├── index-[hash].css        # Main styles
    ├── react-vendor-[hash].js  # React, React Router
    ├── query-vendor-[hash].js  # TanStack Query
    ├── chart-vendor-[hash].js  # Recharts
    ├── signalr-vendor-[hash].js # SignalR
    └── index-[hash].js         # App code
```

Files are embedded in .NET package as `EmbeddedResource` and served via `ManifestEmbeddedFileProvider`.

## Environment Variables

No `.env` files used. Runtime config fetched from backend `/api/config` endpoint.

## Adding New Features

### 1. Add Backend DTO (if needed)
Add type to `src/types/*.types.ts` matching backend DTO.

### 2. Add API Method
Add method to `ApiService` in `src/services/api.ts`.

### 3. Create React Query Hook
Add hook to `src/hooks/use*.ts`.

### 4. Create Component
Add component to `src/components/*/MyComponent.tsx`.

### 5. Add to Page
Import and use component in page.

### 6. Add Route (if new page)
Add route to `src/router.tsx`.

## Troubleshooting

**Build fails with Rollup error on Windows:**
- Fixed: `@rollup/rollup-win32-x64-msvc` is now a required dependency

**SignalR not connecting:**
- Check `signalRHubPath` in config matches backend hub mapping
- Ensure user is authenticated (SignalR connects after login)

**API calls fail with 401:**
- Check username/password in `authStore`
- Check backend `RequireAuthentication` option

**Types don't match backend:**
- Ensure types in `src/types/*.ts` exactly match backend DTOs (camelCase)
- Check enum values match (e.g., `QueuedTaskStatus`)

**Charts not rendering:**
- Check data format matches Recharts requirements
- Ensure `ResponsiveContainer` has width/height
- Check console for errors

## Performance Tips

- Use React.memo for expensive components
- Use `useMemo`/`useCallback` for expensive calculations
- Lazy load pages: `const TasksPage = lazy(() => import('@/pages/TasksPage'));`
- Virtual scrolling for long lists (not yet implemented)
- Debounce search inputs

## Code Style

- Use functional components + hooks (no class components)
- Use TypeScript strict mode (enabled)
- Use Tailwind utilities (avoid custom CSS)
- Use shadcn/ui components (consistent design)
- Use lucide-react icons
- Use date-fns for dates (not moment.js)
- Use Prettier for formatting (configured)
- Use ESLint for linting (configured)

## Related Files

- **Backend**: `../EverTask.Monitor.Api.csproj` (embeds wwwroot)
- **Backend README**: `../README.md`
- **Build Note**: `./BUILD_NOTE.md`
- **Package.json**: `./package.json`
- **TypeScript Config**: `./tsconfig.json`
- **Vite Config**: `./vite.config.ts`
- **Tailwind Config**: `./tailwind.config.js`
