# Tabular Ops Console

Desktop application for managing deployed Power BI / Analysis Services tabular models. A replacement for the operational subset of SSMS — refresh orchestration, partition management, live trace viewing, and refresh failure forensics.

## Context

SSMS is the de facto tool for managing deployed tabular models but is buggy, slow, and has poor UX for the tabular-specific workflows that matter in daily ops work. Tabular Editor (2 and 3) is excellent for *authoring* models but underinvests in *operating* them — no refresh history, no visual partition map, no trace viewer, no refresh failure forensics, no session management.

This tool fills that gap. It is deliberately scoped as an **ops console**, not a model editor. No DAX authoring workflows, no BPA, no calculation group designer — Tabular Editor already does those well.

## Target user

A data engineer or BI consultant who manages 5-15 deployed tabular models at once, across multiple client tenants. Primary daily workflows:

1. Trigger ad-hoc table/partition refreshes, often bulk operations across selected partitions
2. Watch refreshes happen live with useful progress info
3. Diagnose why a refresh failed — see the exact partition, source query, error, and retry state
4. Monitor active sessions and memory usage on a deployed model
5. Query DMVs for troubleshooting

## Tech stack

- **.NET 10** (C#)
- **WPF** (Windows-only, x64 — see ADR-009)
- **Microsoft.AnalysisServices** (TOM) — core model interaction
- **Microsoft.AnalysisServices.AdomdClient** — DAX/MDX/DMV queries
- **Microsoft.Identity.Client** (MSAL.NET) — Entra ID auth with per-tenant token caching
- **Microsoft.PowerBI.Api** — Enhanced Refresh REST API (for Power BI/Fabric models)
- **SQLite** (via `Microsoft.Data.Sqlite`) — local refresh history and trace archive
- Reference code to adapt:
  - `microsoft/Analysis-Services` repo → `AsPartitionProcessing/PartitionProcessor.cs` (refresh orchestration)
  - `microsoft/Analysis-Services` repo → `AsTrace` and `AsXEventSample` (trace collection)

## Architecture principles

1. **Core library has zero UI dependencies.** `TabularOps.Core` should be reusable from a CLI, Dagster sensor, or automation pipeline later.
2. **Per-tenant isolation.** Consulting use — never mix credentials/connections between clients. Each tenant has its own MSAL cache file and its own active `Server` connection.
3. **Active-tenant polling only.** Don't poll every connected tenant continuously — only the currently-viewed one. Other tenants refresh on-demand when switched to.
4. **Route refresh path by endpoint type.** Power BI / Fabric → Enhanced Refresh REST API (supports cancel, retry, timeout). SSAS / AAS → TOM `RequestRefresh` + `SaveChanges`.
5. **Stream trace events to UI via `Channel<T>`.** Don't block the trace subscription thread on UI work.
6. **Sensitive decisions persist.** Don't re-ask Entra ID consent every session. MSAL token cache to disk, encrypted per OS conventions.

## Project layout

```
TabularOps/
├── TabularOps.sln
├── src/
│   ├── TabularOps.Core/              # class library, no UI refs
│   │   ├── Connection/
│   │   │   ├── ConnectionManager.cs
│   │   │   ├── TenantContext.cs
│   │   │   └── MsalTokenCache.cs
│   │   ├── Refresh/
│   │   │   ├── IRefreshEngine.cs
│   │   │   ├── TomRefreshEngine.cs   # for SSAS/AAS
│   │   │   ├── PowerBiRefreshEngine.cs # for PBI/Fabric
│   │   │   ├── PartitionProcessor.cs # adapted from AsPartitionProcessing
│   │   │   └── RefreshHistoryStore.cs
│   │   ├── Tracing/
│   │   │   ├── TraceCollector.cs     # adapted from AsTrace
│   │   │   ├── TraceEvent.cs
│   │   │   └── TraceEventChannel.cs
│   │   ├── Dmv/
│   │   │   ├── DmvQueries.cs
│   │   │   └── PartitionCacheStore.cs  # SQLite cache for fast model switching
│   │   └── Model/                    # POCOs: ModelRef, PartitionRef, etc.
│   │
│   └── TabularOps.Desktop/           # WPF (Windows-only, x64)
│       ├── Views/
│       ├── ViewModels/
│       └── App.xaml
│
├── tests/
│   └── TabularOps.Core.Tests/
│
└── docs/
    ├── ui-design.md
    ├── decisions.md
    └── ui-prototype/
        └── tabular-ops-console.html  # clickable UI reference
```

## Build priority (MVP path)

Do NOT try to build everything at once. Follow this order — each milestone is usable on its own.

### ✅ Milestone 1: Connect and browse — DONE (commit 76aaaed)
- `ConnectionManager` with MSAL interactive login + per-tenant token cache (DPAPI-encrypted on Windows)
- Sidebar model tree grouped by workspace, populated from `Server.Databases` via TOM
- Topbar tenant pill with connection status dot
- Status bar: connection state + endpoint type + read-only badge
- `AddConnectionDialog`: Power BI (OAuth → workspace picker) and SSAS/AAS (connection string) flows
- Entra UPN shown above workspace list in sidebar after login

### ✅ Milestone 2: Partition map — DONE (commit 34d7550)
- Horizontal layout: table info panel on left (name, rows, size, last refresh, partition count), partition tiles on right
- Two-phase loading: TOM structure first (fast), DMV stats enriched async
- Partition tiles color-coded by state: teal=OK, amber=stale (dimmed), red=failed, blue=refreshing/queued
- Fill bar at tile bottom shows relative partition size within the table
- Click table info panel to select/deselect all partitions; click tiles to select individually

### ✅ Milestone 3: Refresh engine — DONE (commit 3e850c4)
- `TomRefreshEngine`: `RequestRefresh` + `SaveChanges` on background thread; `db.Refresh()` after commit to sync `RefreshedTime`
- Refresh type selector: Default / Full / Data only / Calculate / Clear
- `RefreshHistoryStore`: SQLite-backed; `LogStartAsync` / `LogCompleteAsync` per partition
- History view with scatter chart (time-of-day × duration), run list, status badges
- Power BI workspace sync: fetches refresh history from Enhanced Refresh REST API, deduplicates by `requestId`
- History filtered by model when a model is selected; workspace-level when only a workspace is selected
- Orphaned `Running` entries (app crash during refresh) auto-cancelled on startup after 30 min

### ✅ Milestone 4: UX polish — DONE (commit 61fbaae)
- **Partition cache** (`PartitionCacheStore`): SQLite-backed; Phase 0 loads cache instantly on model switch; live data updates in background with pulsing progress bar indicator
- **Refresh confirm dialog**: modal showing affected partitions grouped by table, refresh type, Cancel/Refresh with keyboard support
- **Workspace context bar**: persistent row above tab strip showing workspace, endpoint type, CL level, model count; Fabric capacity name/SKU/region fetched from Power BI Capacities API and persisted
- **ComboBox theming**: full custom `ControlTemplate` for ComboBox + ComboBoxItem using `DynamicResource` colors
- **Light/dark theme toggle**: runtime swap via `ThemeManager`; preference persisted to disk; all controls use `{DynamicResource}` throughout

### ✅ Milestone 5: Live trace — DONE (commit eb524e4)
- Trace subscription adapted from `AsTrace`; events streamed to UI via `Channel<TraceEvent>`
- Trace list with detail pane (scope, duration, CPU, rows, error code, session ID)
- Event filter chips (Progress / Query / Errors / Lock / Audit)
- Events persisted to SQLite (`trace.db`, 30-day rolling archive)
- Trace auto-stops when switching away from the tab, resumes on return

### ✅ Milestone 6: GUI alignment with spec — DONE (commit 4a7fdd1)
- JetBrains Mono Bold/SemiBold fonts downloaded; all weight variants now render correctly
- Light theme color fixes (bg-1 was pure white, making sidebar indistinguishable from content)
- Startup performance: ADOMD connections deferred to first use, halving network round-trips
- Removed duplicate Connect button from sidebar footer
- Partition map redesigned: floating selection tray replaces toolbar ComboBox; per-mode refresh buttons
- Overview tab added: metric cards (size, rows, partitions, tables) + detail rows (last processed, memory, CL, endpoint, capacity SKU/name/region)
- Process model dropdown in Overview toolbar for whole-model refreshes in any mode
- All unimplemented buttons disabled with "Coming soon" tooltips

### ✅ Milestone 7: Backup — DONE (PR #2, release v0.7.0)
- `BackupService` + `BackupStore`: TOM `Database.Backup()` → ABF file, SQLite run history
- Overview toolbar: "Backup model" → file name only (the server owns the storage location; no SaveFileDialog) → last backup date updated in UI

### ✅ Milestone 8: Power BI Enhanced Refresh path — DONE (PR #4, release v0.8.0)
- `RefreshDispatcher` routes Power BI / Fabric to the Enhanced Refresh REST API, SSAS/AAS to TOM
- `PowerBiRefreshEngine`: POST `/refreshes` → poll status → DELETE on cancel
- Cancel button in the partition map selection tray (the thing SSMS can't do)
- Left-click a sidebar model opens it in the current tab; right-click → new tab
- Self-contained single-file `win-x64` exe (no runtime install), portable mode via `portable.flag`, all local paths via `AppPaths`
- Upgraded to .NET 10; tag pushes build and publish a pre-release automatically

### Milestone 9+ (nice to have, not MVP)
- Retry/timeout configuration per refresh (deferred from M8; `RetryCount` is currently hard-coded to 0)
- Sessions view (via `DISCOVER_SESSIONS`)
- Memory breakdown view (Vertipaq-style)
- DAX query window
- Trend charts on refresh history
- Role impersonation (explicitly deferred — not in MVP)

## Non-goals

- Model editing (that's Tabular Editor)
- DAX authoring IDE (that's TE3 or DAX Studio)
- Calculation group designer
- BPA (Best Practice Analyzer)
- CI/CD deployment pipelines (use Tabular Editor CLI for that)

## Key references

- `docs/ui-prototype/tabular-ops-console.html` — interactive UI mockup, open in browser
- `docs/ui-design.md` — design decisions behind the UI
- `docs/decisions.md` — architectural decisions and open questions
- [microsoft/Analysis-Services](https://github.com/microsoft/Analysis-Services) — clone this for reference code
- [TabularEditor/TabularEditor](https://github.com/TabularEditor/TabularEditor) — reference for TOM usage patterns (WinForms, MIT-licensed)
- [Power BI Enhanced Refresh docs](https://learn.microsoft.com/en-us/power-bi/connect-data/asynchronous-refresh)

## Known gotchas

- Power BI XMLA trace subscriptions have different event availability than SSAS. Test `AsTrace`-derived code against a real Power BI Premium workspace in Milestone 4, not in Milestone 5. If events are limited, fall back to xEvents via `AsXEventSample` pattern.
- Power BI XMLA endpoints require Premium/PPU/Fabric capacity for read-write. Read-only works on any workspace but limits the app to browse-only mode.
- MSAL token cache encryption is OS-specific. On Windows use DPAPI via `Microsoft.Identity.Client.Extensions.Msal`. On macOS use keychain. On Linux, no good default — document this limitation.
- TOM `Model.SaveChanges()` is synchronous and blocking. Run it on a background thread with cancellation token support. The Enhanced Refresh REST API is async natively.
