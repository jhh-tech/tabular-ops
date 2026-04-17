# UI Design

## Aesthetic direction

**Utilitarian, industrial, dark, information-dense.** This is a tool used 4-8 hours/day by technical operators. No decorative gradients, no rounded friendliness, no marketing polish. Closer to k9s, Azure Data Studio, or Grafana than to SSMS.

## Color tokens

```css
--bg-0: #0a0d10;     /* app background */
--bg-1: #11151a;     /* sidebar, topbar, status bar */
--bg-2: #161b21;     /* cards, cells */
--bg-3: #1e242c;     /* hover states, pills */
--border: #2a313b;
--border-strong: #3a424e;

--text-0: #e8ecef;   /* primary text */
--text-1: #aab3bd;   /* secondary text */
--text-2: #6b7684;   /* tertiary, labels */
--text-3: #4a5360;   /* disabled, separators */

--accent: #4ec9b0;        /* teal — active/primary */
--accent-dim: #2f7a6a;
--warn: #e4a853;          /* amber — stale, warnings */
--error: #e06c75;         /* red — failures */
--success: #98c379;       /* green — ok */
--info: #61afef;          /* blue — refreshing, info */
--violet: #c586c0;        /* progress events in trace */
```

## Typography

- **Sans (UI chrome):** IBM Plex Sans — distinctive but readable for tool UIs
- **Mono (all data, IDs, paths, timestamps):** JetBrains Mono — tall x-height, clear zeros
- Use mono for anything a user might copy to another tool (partition names, error IDs, timestamps, file paths, connection strings, DAX, SQL)
- Use sans for labels, buttons, navigation

## Layout

```
┌──────────────────────────────────────────────────────────────┐
│ TOPBAR: logo · tenant · workspace · connection · user       │  38px
├─────────────┬────────────────────────────────────────────────┤
│             │ BREADCRUMB: tenant / workspace / model         │
│  SIDEBAR    ├────────────────────────────────────────────────┤
│             │ TABS: Partitions · Trace · History · Sessions  │
│  - Views    ├────────────────────────────────────────────────┤
│  - Models   │                                                │
│    grouped  │  CONTENT (view-specific)                       │
│    by       │                                                │
│    tenant   │                                                │
│             │                                                │
│             │                                                │
├─────────────┴────────────────────────────────────────────────┤
│ STATUSBAR: connection · memory · sessions · queue · version │  22px
└──────────────────────────────────────────────────────────────┘
```

## Key design decisions

### Tenant isolation is visually prominent
For consulting use, mixing up production tenants is the #1 risk. The topbar has a large tenant switcher with a colored status dot. The sidebar groups models by tenant with clear separators. Breadcrumb always shows tenant → workspace → model.

### Partition grid as the headline view
Each table gets a row:
- Table name + metadata (rows, size, partition count, policy)
- Dense grid of partition cells below

Each partition cell encodes state three ways:
- Border color (neutral / teal=selected / blue=refreshing / red=failed)
- Background tint (same scheme, subtler)
- Fill bar at bottom showing relative size

Stale partitions dim to 55% opacity. At a glance you see "24 fine, 1 pulsing, 1 red" without reading text.

### Selection tray slides up on selection
When user selects partitions across any tables, a tray slides up from the bottom with:
- Selection count
- Estimated rows and duration
- Primary actions: Refresh (full), Refresh (data only), Merge
- Danger action: Drop

Tray is fixed-positioned, does not scroll with content.

### Live Trace is a grid + detail pane
Top: streaming event list with columns `timestamp · event · object · message · duration · rows`. Event type color-coded.

Bottom: detail pane shows the full event context — for errors, this means the partition, source SQL, error text, retry state. This is the "refresh failure forensics" that SSMS hides.

### Status bar is always visible
Memory use, active sessions, refresh queue depth, connection mode. Glanceable numbers that inform whether to trigger another operation.

## Interaction patterns

- **Keyboard first** — every action has a shortcut, shown as `<kbd>` hint in buttons
- **`/` focuses the search** in any view
- **Click-to-select partitions, cmd/ctrl-click for multi-select, shift-click for range**
- **Tabs preserve state per model** — switching models resets tab scroll positions, switching tabs within a model doesn't
- **No modals for destructive actions** — use inline confirmation in the selection tray ("Drop 3 partitions? [Confirm] [Cancel]")

## Things deliberately NOT in the UI

- No dashboard/home screen with charts — ops tool, not BI report
- No settings panel on the main window — use a separate preferences window
- No notifications/toasts — events appear in the trace view
- No wizard flows — power users, show them everything
