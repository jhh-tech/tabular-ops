# Tabular Ops

Desktop console for managing deployed Power BI / Analysis Services tabular models. Covers the operational workflows SSMS handles poorly: refresh orchestration, partition management, live trace viewing, and refresh failure forensics.

See [`CLAUDE.md`](CLAUDE.md) for full context, architecture, and the milestone roadmap.

---

## Prerequisites

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- A Power BI workspace with XMLA enabled (Premium, PPU, or Fabric capacity) **or** an SSAS/AAS instance you can reach over the network

---

## Setup

### 1. Register an Entra app

The app uses MSAL's browser popup for Power BI authentication. You need an app registration in your tenant:

1. Go to [Entra portal](https://entra.microsoft.com) → **App registrations** → **New registration**
2. Name it something like `Tabular Ops Dev`, set redirect URI to `http://localhost` (Public client / native)
3. Under **API permissions**, add `Power BI Service → Delegated → Dataset.ReadWrite.All` (or `.Read.All` for read-only)
4. Copy the **Application (client) ID**

Open `appsettings.json` at the repo root and paste it in:

```json
{
  "EntraClientId": "your-client-id-here"
}
```

> SSAS/AAS connections use Windows auth and don't require an app registration.

### 2. Add fonts

The UI uses IBM Plex Sans (UI chrome) and JetBrains Mono (data, IDs, timestamps). WPF embeds these as resources — they must be present at build time.

Download and copy `.ttf` files into `src/TabularOps.Desktop/Assets/Fonts/`:

| Font | Download |
|---|---|
| IBM Plex Sans | [github.com/IBM/plex](https://github.com/IBM/plex/releases) — grab `IBMPlexSans-Regular.ttf`, `IBMPlexSans-SemiBold.ttf` |
| JetBrains Mono | [jetbrains.com/lp/mono](https://www.jetbrains.com/lp/mono/) — grab `JetBrainsMono-Regular.ttf` |

The app will compile and run without the fonts (WPF silently falls back to a system font), but the UI will look wrong.

### 3. Build and run

```bash
dotnet build
dotnet run --project src/TabularOps.Desktop
```

---

## Project layout

```
TabularOps/
├── src/
│   ├── TabularOps.Core/              # Class library — no UI dependencies
│   │   ├── Connection/               # ConnectionManager, TenantContext, MsalTokenCache
│   │   ├── Model/                    # POCOs: ModelRef, PartitionRef, EndpointType
│   │   ├── Refresh/                  # IRefreshEngine, TomRefreshEngine, PowerBiRefreshEngine
│   │   ├── Tracing/                  # TraceCollector, TraceEvent, TraceEventChannel
│   │   └── Dmv/                      # DmvQueries
│   └── TabularOps.Desktop/           # WPF application
│       ├── Themes/                   # Colors, typography, control styles, converters
│       ├── Converters/               # IValueConverter implementations
│       ├── ViewModels/               # CommunityToolkit.Mvvm ViewModels
│       ├── Views/                    # XAML windows and dialogs
│       └── Assets/Fonts/             # IBM Plex Sans + JetBrains Mono (add manually)
├── tests/
│   └── TabularOps.Core.Tests/        # xUnit — Core logic only, no UI
└── docs/
    ├── decisions.md                  # Architecture decisions (ADR-001 through ADR-016)
    ├── ui-design.md                  # Color tokens, typography, layout spec
    └── ui-prototype/
        └── tabular-ops-console.html  # Clickable UI reference — open in browser
```

---

## Architecture notes

**Three connections per tenant** (see ADR-002 in `docs/decisions.md`):

| Connection | Type | Purpose |
|---|---|---|
| TOM write | `Microsoft.AnalysisServices.Server` | `RequestRefresh`, `SaveChanges`, schema reads |
| ADOMD poll | `AdomdConnection` | Background DMV queries (sessions, memory) — active tenant only |
| Trace | `Microsoft.AnalysisServices.Server` | Owned by `TraceCollector`; never used for commands |

TOM `Server` is not thread-safe — sharing a connection between a blocking `SaveChanges()` and a background poll causes races. The three-connection split is intentional.

**Refresh routing** (ADR-003): connection strings starting with `powerbi://` route to the Enhanced Refresh REST API; everything else goes through TOM `RequestRefresh`. The Power BI path supports cancellation; the TOM path cancels via `Server.CancelProcess(sessionId)` on a separate connection.

**SQLite** (ADR-004): one DB file per tenant in `%LocalAppData%\TabularOps\`. WAL mode is mandatory. Trace events are batch-inserted (every 500ms or 50 events) — never per-event.

---

## Milestone status

| Milestone | Status | Description |
|---|---|---|
| 1 — Connect and browse | **Done** | MSAL auth, sidebar model tree, shell UI |
| 2 — Partition map | **Done** | Partition tiles from TOM + DMV stats, selection, fill bar |
| 3 — Refresh engine | **Done** | TOM refresh, SQLite history, Power BI Enhanced Refresh history sync |
| 4 — UX polish | **Done** | Partition cache, refresh confirm dialog, workspace context bar, theming |
| 5 — Live trace | **Done** | Event streaming, filtering, SQLite persistence |
| 6 — Power BI Enhanced Refresh | Pending | Cancel support, REST API refresh path |

---

## Known limitations

- **Windows x64 only.** `Microsoft.AnalysisServices.NetCore.retail.amd64` is a native AMD64 binary. Cross-platform is not possible with this library regardless of UI framework.
- **Power BI XMLA requires Premium/PPU/Fabric capacity** for read-write. Read-only XMLA works on any workspace but disables refresh actions (shown in the status bar).
- **Token cache on Linux** uses a plaintext fallback. Not relevant for MVP (Windows-only) but documented in ADR-015 in `docs/decisions.md`.
- **Minimum compatibility level: 1500** (SQL Server 2019 AS, AAS, Power BI XMLA). Connecting to older instances will surface an error at connection time.
