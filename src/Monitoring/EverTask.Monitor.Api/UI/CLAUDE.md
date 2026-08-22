# EverTask Monitor Dashboard UI

Refer to the root CLAUDE.md for project-wide rules.

React + TypeScript SPA built into `../wwwroot/` and embedded in the `EverTask.Monitor.Api` package. Stack:
Vite, Tailwind, shadcn/ui, TanStack Query (server state), Zustand (client state), react-router-dom and
`@microsoft/signalr`. Versions live in `package.json` — do not restate them here.

## Build

- **pnpm only** — `pnpm-lock.yaml` and `pnpm-workspace.yaml` are committed and there is no
  `package-lock.json`, so `npm install` resolves off-lockfile. `pnpm install` / `pnpm run dev` (port 5173,
  proxies `/evertask-monitoring/{api,hub}` to `:5000`) / `pnpm run build`.
- `pnpm run lint` gates CI at `--max-warnings 0` and runs before the build.
- shadcn components: `npx shadcn@latest add <name>` → `src/components/ui/`.

## Gotchas

- **The base path `/evertask-monitoring/` is hardcoded in three places that must agree**: `base` in
  `vite.config.ts`, `basename` in `src/router.tsx`, and the 401 redirect in `src/services/api.ts`. A
  mismatch breaks asset loading after a reload.
- **The pnpm `overrides` list is duplicated**: `package.json` (read by pnpm 9, the version pinned in
  `.github/workflows/build.yml`) and `pnpm-workspace.yaml` (read by pnpm >= 10). Keep both in sync.
- The axios response interceptor rewrites enum strings to numbers (`convertStatusStringsToNumbers` in
  `src/services/api.ts`); `baseURL` and the SignalR hub path are NOT hardcoded but fetched at runtime from
  `/api/config`, which is what keeps the app working behind the prefix.
- Types in `src/types/*.types.ts` must mirror the backend DTOs: camelCase JSON, enums arriving as strings.

## Layout

Zustand stores in `src/stores` (auth persisted to localStorage, realtime capped at the last 200 events);
React Query hooks in `src/hooks`; axios / SignalR / config clients in `src/services`.
