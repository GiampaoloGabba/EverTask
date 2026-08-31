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
- **`pnpm test`** (Vitest + Testing Library + jsdom, config in `vitest.config.ts`, which merges
  `vite.config.ts` so a component under test resolves exactly as the one that ships). Tests live beside what
  they exercise, in `src/**/__tests__/*.test.tsx`, and `tsc` in the build type-checks them with the rest.
  Behaviour the compiler and the linter cannot see belongs here: paging, the badges and what makes them
  appear, which query keys an event invalidates, and what a panel shows when a count is zero. Mock at the
  HTTP boundary (`@/services/api`) and let the hook, the query client and the component be real.
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
  `QueuedTaskStatus` is the exception the interceptor rewrites to numbers; `occurrenceMode`, `misfirePolicy`
  and `misfireKind` stay strings and are typed as string unions.
  - **A nullable field is an OPTIONAL key here** (`name?: T | null`), because the API omits a null rather
    than writing it and the axios interceptor restores nothing: a one-shot's response carries no
    `parentTaskId` key at all, so `parentTaskId: string | null` promises a value the wire never sends. Which
    keys are absent is asserted server-side, on the raw JSON, by
    `test/EverTask.Tests.Monitoring/API/Controllers/JsonContractTests.cs` — which also reads these files back
    and fails when a nullable DTO field is mirrored as a required key.
  - **`signalr.types.ts` mirrors a DIFFERENT wire and no DTO.** `EverTaskEventData` is EverTask's own
    monitoring record, sent verbatim by `SignalRTaskMonitor`, so the DTO walk above cannot see it and a field
    added to the record is mirrored by nobody unless someone does it here. That hub does NOT omit nulls
    (SignalR's `JsonHubProtocol` has no `WhenWritingNull`): every key is present, holding null, so a consumer
    discriminates on the VALUE and not on the key. The keys are optional all the same, to keep one rule for
    these files. Pinned on the live hub by
    `test/EverTask.Tests.Monitoring/SignalR/EventWireContractTests.cs`, which fails when the wire carries a
    key this file does not declare.
- **`Late` is computed in the browser, not served.** The API only ever reports the PERSISTED misfire kind
  (`CatchUp` / `FireOnce`), which is a fact of the decision that created the row; lateness is the distance
  between an occurrence's nominal slot and the moment it started, so `components/tasks/OccurrenceBadges.tsx`
  derives it. Nothing under a second is shown.
  - **The second term is `startedAtUtc`, never `lastExecutionUtc`.** That column is written only on terminal
    transitions, so it says when the run ENDED: subtracting the slot from it reported a punctual occurrence
    with a three-minute handler as three minutes late. `startedAtUtc` is the start the API reports for the
    row — its recorded `InProgress` transition, or, for a run that completed and really measured a duration,
    its end less that duration (a finalization writes an end with no duration, on a row no handler ran for). A row that has no start yet is measured against the clock only while it can still
    start (`status` is one of the waiting ones); a cancelled occurrence never started, so it shows nothing
    instead of a number that grows for ever. The API answers `null` wherever nothing measured a start, and
    `null` means NO badge — never a fallback to another column.
- **Every query key an event can invalidate belongs in `hooks/useSignalRRefresh.ts`.** `['occurrences', id]`
  is invalidated unconditionally with `['taskCounts']`: the Occurrences tab declares no `refetchInterval`
  and the global `staleTime` is 30 s, so a key missing from that hook is a panel that never updates while
  everything around it does. The page belongs to that key (`['occurrences', id, skip, take]`) because the
  paging is the SERVER's: prefix invalidation still reaches every page.
- **A list the storage pages needs a control to page it.** `OccurrencesTab` holds its own `skip` and offers
  Previous / Next: a schedule with three hundred occurrences showed the hundred most recent and nothing
  could reach the failed one at row 150. The empty state belongs to the FIRST page only — an empty page
  further in is a series that shrank under the reader, and it still needs its way back.
  - **`AuditTrailTab` is the same rule for the detail's two audit tabs** (`trail="status" | "runs"`), which
    the API pages too since 4.0.0. It reads the ENDPOINTS (`['statusAudits' | 'runsAudits', id, skip, take]`),
    never `task.statusAudits` / `task.runsAudits` — those carry only the first page — while the tab LABELS
    read `task.statusAuditsTotalCount` / `runsAuditsTotalCount`, the totals the detail reports. The runs
    timeline keeps reading oldest-first WITHIN the page it shows; the page itself is newest-first, like the
    endpoint.

## Layout

Zustand stores in `src/stores` (auth persisted to localStorage, realtime capped at the last 200 events);
React Query hooks in `src/hooks`; axios / SignalR / config clients in `src/services`.
