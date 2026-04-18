# UI Design

## Aesthetic direction

**Utilitarian, industrial, information-dense.** This is a tool used 4-8 hours/day by technical operators. No decorative gradients, no rounded friendliness, no marketing polish. Closer to k9s, Azure Data Studio, or Grafana than to SSMS.

The app ships with both a dark and a light theme. Dark is the default. Theme is toggled via a button in the topbar and persisted to disk. All controls use `{DynamicResource}` throughout — no hardcoded colors.

## Color tokens

### Dark theme (default)

```
bg-0:          #0a0d10     app background
bg-1:          #11151a     sidebar, topbar, status bar
bg-2:          #161b21     cards, cells
bg-3:          #1e242c     hover states, pills

border:        #2a313b
border-strong: #3a424e

text-0:        #e8ecef     primary text
text-1:        #aab3bd     secondary text
text-2:        #6b7684     tertiary, labels
text-3:        #4a5360     disabled, separators

accent:        #4ec9b0     teal — active/primary actions
accent-dim:    #2f7a6a
warn:          #e4a853     amber — stale, warnings
error:         #e06c75     red — failures
success:       #98c379     green — completed
info:          #61afef     blue — refreshing, info
violet:        #c586c0     progress events in trace
```

### Light theme

```
bg-0:          #f5f7fa     app background
bg-1:          #ebeef2     sidebar, topbar, status bar
bg-2:          #ffffff     cards, cells
bg-3:          #dde2e9     hover states, pills

border:        #c8cdd5
border-strong: #b0b8c4

text-0:        #1a2130     primary text
text-1:        #3d4a5c     secondary text
text-2:        #6b7684     tertiary, labels
text-3:        #9ba5b0     disabled, separators

accent:        #1a9e88     teal — active/primary actions
accent-dim:    #d0f0eb
warn:          #b87816     amber — stale, warnings
error:         #c0392b     red — failures
success:       #2e8b57     green — completed
info:          #2271b3     blue — refreshing, info
```

## Typography

- **Sans (UI chrome):** IBM Plex Sans — distinctive but readable for tool UIs
- **Mono (all data, IDs, paths, timestamps):** JetBrains Mono — tall x-height, clear zeros
- Use mono for anything a user might copy to another tool (partition names, error IDs, timestamps, file paths, connection strings, DAX, SQL)
- Use sans for labels, buttons, navigation

Both fonts are embedded as WPF resources (`Build Action: Resource`) and registered in `App.xaml` via `FontFamily` resource dictionary entries. WPF does not fall back gracefully to system fonts — it silently substitutes.

## Layout

```
┌──────────────────────────────────────────────────────────────┐
│ TOPBAR: logo · sidebar toggle · [spacer] · theme · user     │  38px
├─────────────┬────────────────────────────────────────────────┤
│             │ CONTEXT BAR: workspace · type · CL · N models │  32px (always visible)
│  SIDEBAR    ├────────────────────────────────────────────────┤
│             │ TABS: Partitions · History                     │  36px
│  - Workspaces ├──────────────────────────────────────────────┤
│    grouped  │                                                │
│    by       │  CONTENT (view-specific)                       │
│    tenant   │                                                │
│             │                                                │
│             │                                                │
├─────────────┴────────────────────────────────────────────────┤
│ STATUSBAR: connection mode · endpoint type · read-only      │  22px
└──────────────────────────────────────────────────────────────┘
```

## Workspace context bar

A 32px row above the tab strip, always visible regardless of which tab is active. Shows:

- **Workspace name** (primary text)
- **Endpoint type badge**: `Power BI`, `Azure AS`, or `SQL Server AS` (secondary text)
- **Compatibility level**: `CL 1605` format (secondary text)
- **Model count**: `N models` in the workspace (secondary text)
- **Fabric capacity section** (only when connected to a Premium/Fabric workspace):
  - SKU badge (e.g. `F64`) in accent-dim background
  - Capacity name
  - Capacity region

Capacity metadata (name, SKU, region) is fetched from the Power BI Capacities REST API on first connect and persisted to `connections.json` so it survives restarts without re-fetching.

## Key design decisions

### Tenant isolation is visually prominent
For consulting use, mixing up production tenants is the #1 risk. The sidebar groups workspaces by tenant with a status dot (grey=connecting, green=connected, red=error). The context bar always shows which workspace and model is active.

### Partition grid as the headline view
Each table gets a row:
- Left panel: table name + aggregate stats (row count, total size, partition count, last refresh time)
- Right: dense grid of partition tiles

Each partition tile encodes state three ways:
- Border color
- Background tint
- Fill bar at bottom showing relative size within the table

| State | Border color | Background tint | Opacity |
|---|---|---|---|
| OK | `border` | none | 100% |
| Stale | `warn` amber | `warn` dim tint | 65% |
| Failed | `error` red | `error` dim tint | 100% |
| Refreshing | `info` blue | `info` dim tint | 100% (pulse animation) |
| Queued | `info` dim | none | 80% |

Tiles are clickable. Click a tile to select/deselect it. Click the table info panel on the left to select/deselect all partitions in that table (toggles all if any unselected, deselects all if all selected).

### Three-phase partition loading
1. **Phase 0** (instant): Load last-known partition data from SQLite cache (`PartitionCacheStore`). The grid renders immediately on model switch. A pulsing amber/blue bar indicates live data is loading.
2. **Phase 1** (~1–3s): Fetch live partition names and states from TOM. Updates the grid.
3. **Phase 2** (~2–5s): Fetch row counts and sizes from `$SYSTEM.DISCOVER_STORAGE_TABLES` DMV. Enriches tiles with stats.

The cache is written after Phase 2 completes. Next model switch for the same model starts at Phase 0 with fresh data.

### Refresh confirm dialog (modal)
When the user clicks "Refresh selected", a modal dialog appears before the refresh starts. It shows:
- A summary line: "Refresh N partition(s) across M table(s)"
- The selected refresh type (highlighted in accent color)
- A scrollable list of affected partitions grouped by table

The user can confirm or cancel. This replaces the original inline "selection tray" concept — a modal is clearer for a destructive operation and avoids accidental refreshes.

### Refresh type selector
A ComboBox above the Refresh button lets the user select the refresh mode:
- Default (Automatic)
- Full
- Data only
- Calculate
- Clear

The ComboBox uses a full custom `ControlTemplate` with `{DynamicResource}` references so it participates in the light/dark theme. System Chrome overrides simple property setters on WPF's ComboBox.

### History view
Two panels side by side:
- **Scatter chart**: each dot is one refresh run, x-axis = time of day, y-axis = duration. Color-coded by status.
- **Run list**: most recent runs first, showing table, partition, status badge, started time, duration.

Filtering: when a model is selected in the sidebar, history is filtered to that model's database. When only a workspace is selected (no model), workspace-level refreshes from the Power BI Enhanced Refresh API are shown.

Power BI workspace refresh history is imported from the Enhanced Refresh REST API and deduplicated by `requestId` (stored as `external_id` in SQLite).

### Status bar is always visible
Shows connection mode, endpoint type, and read-only state. Glanceable indicator of whether write operations are available.

## Interaction patterns

- **Click to select a partition tile, click the table info panel to select/deselect all in a table**
- **Ctrl+click / shift+click for multi-select is NOT implemented (yet)** — single-click toggles only
- **"Clear selection" button** appears in the toolbar when partitions are selected
- **Tabs preserve context** — History tab remembers the filter context (workspace or model level) and re-applies it on switch

## Things deliberately NOT in the UI

- No dashboard/home screen with charts — ops tool, not BI report
- No DAX authoring — that's Tabular Editor or DAX Studio
- No notifications/toasts — refresh status updates in the partition map toolbar
- No wizard flows — power users, show them everything
- No inline confirmation for refresh — always uses the modal dialog
