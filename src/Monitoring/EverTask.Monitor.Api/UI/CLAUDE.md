# CLAUDE.md - EverTask Monitor Dashboard UI

React TypeScript SPA for EverTask monitoring. Embedded in `EverTask.Monitor.Api` NuGet package.

## Quick Facts

- **Stack**: React 18 + TypeScript 5 + Vite 5 + Tailwind CSS 3
- **State**: TanStack Query 5 (server), Zustand 4 (client)
- **Routing**: React Router 6
- **UI**: shadcn/ui components
- **Real-time**: @microsoft/signalr 8
- **Build Target**: `../wwwroot/` (embedded in .NET package)

## Build

```bash
npm install
npm run dev     # → http://localhost:5173 (proxies to :5000)
npm run build   # → ../wwwroot/
```

## Architecture

### Auth Flow (JWT)
1. User visits `/login` → `LoginPage.tsx`
2. Calls `POST /api/auth/login` with username/password
3. Receives JWT token + expiration → saves to `authStore` (localStorage)
4. All API calls include `Authorization: Bearer ${token}` header
5. On 401 → auto-logout + redirect to `/login`

### State Management
- **authStore** (Zustand): `token`, `username`, `expiresAt`, `isAuthenticated`
- **realtimeStore** (Zustand): SignalR events (max 200)
- **configStore** (Zustand): Runtime config from `/api/config`
- **Server state**: TanStack Query (caching, refetch)

### API Service (`src/services/api.ts`)
- Axios-based, auto-inits from `/api/config`
- Request interceptor: adds `Bearer ${token}` header
- Response interceptor: converts enum strings → numbers, handles 401

### SignalR (`src/services/signalr.ts`)
- Auto-connects when authenticated
- Hub: `/evertask-monitoring/hub` (fixed)
- Events → `realtimeStore.addEvent()`

## Project Structure

```
src/
├── components/
│   ├── common/      # Reusable (TaskStatusBadge, JsonViewer, Timeline)
│   ├── dashboard/   # Dashboard (KPICard, Charts, ActivityFeed)
│   ├── layout/      # Layout (Header, Sidebar, MainLayout)
│   ├── tasks/       # Tasks (TasksTable, TaskFilters, TaskDetailModal)
│   └── ui/          # shadcn/ui (auto-generated)
├── pages/           # OverviewPage, TasksPage, LoginPage, etc.
├── hooks/           # React Query hooks (useTasks, useDashboard, useQueues)
├── stores/          # Zustand (auth, realtime, config)
├── services/        # API (api.ts, signalr.ts, config.ts)
├── types/           # TS interfaces (match backend DTOs, camelCase)
├── utils/           # Helpers (dates, status colors, etc.)
├── router.tsx       # React Router config
├── App.tsx          # Main component
└── main.tsx         # Entry point
```

## Critical Type Matching

**TypeScript types MUST match backend C# DTOs (camelCase JSON)**:
```typescript
// src/types/task.types.ts
export interface TaskListDto {
  id: string;                    // Guid in C#
  status: QueuedTaskStatus;      // Enum (0-7)
  createdAtUtc: string;          // DateTimeOffset → ISO string
  isRecurring: boolean;
  recurringInfo: string | null;
}
```

## Key Patterns

### React Query Hook
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
```

### Protected Routes
```typescript
// src/router.tsx
{
  path: '/',
  element: <ProtectedRoute><MainLayout /></ProtectedRoute>,
  children: [
    { index: true, element: <OverviewPage /> },
    { path: 'tasks', element: <TasksPage /> },
  ]
}
```

### shadcn/ui Components
```bash
npx shadcn@latest add button
npx shadcn@latest add card
# → src/components/ui/
```

## Status Colors (Consistent)

```typescript
WaitingQueue: 'bg-blue-500'
Queued: 'bg-blue-500'
InProgress: 'bg-yellow-500'
Pending: 'bg-purple-500'
Completed: 'bg-green-500'
Failed: 'bg-red-500'
Cancelled: 'bg-gray-500'
ServiceStopped: 'bg-gray-500'
```

## Operational Checklist

### Adding New API Endpoint
1. Add method to `apiService` in `src/services/api.ts`
2. Add types in `src/types/*.types.ts`
3. Create React Query hook in `src/hooks/use*.ts`
4. Use hook in component

### Adding New Page
1. Create component in `src/pages/MyPage.tsx`
2. Add route to `src/router.tsx`
3. Add nav link in `src/components/layout/Sidebar.tsx`

### Adding New Chart
1. Use Recharts components (`LineChart`, `PieChart`, etc.)
2. Wrap in `<ResponsiveContainer width="100%" height={300}>`
3. Apply consistent status colors

## Gotchas

1. **Enum Conversion**: `apiService` auto-converts status strings → numbers (see `convertStatusStringsToNumbers()`)
2. **Login Auth**: Must call `/api/auth/login` to get JWT, not Basic Auth
3. **Vite Base Path**: `base: '/evertask/'` in `vite.config.ts` must match backend `BasePath`
4. **SignalR Path**: Fixed to `/evertask-monitoring/hub`, cannot change
5. **Rollup Windows**: Requires `@rollup/rollup-win32-x64-msvc` (package.json dependency)

## Troubleshooting

- **401 errors**: Check JWT token validity (`authStore.expiresAt`)
- **SignalR not connecting**: Ensure authenticated, check hub path
- **Types mismatch**: Verify TypeScript types match backend DTOs (camelCase)
- **Charts not rendering**: Check data format, `ResponsiveContainer` size

## Related Files

- **Backend**: `../EverTask.Monitor.Api.csproj` (embeds wwwroot)
- **Backend Docs**: `../CLAUDE.md`, `../README.md`
- **User Docs**: `E:/Archivio/Sviluppo/Web/EverTask/docs/monitoring-dashboard.md`
