# Architecture Decisions

## Decided

### ADR-001: Core library has no UI dependencies
`TabularOps.Core` is a class library with no WPF/Avalonia references. All UI-facing data flows through plain POCOs and `Channel<T>` or `IObservable<T>` streams. This makes the library reusable from a CLI, Dagster sensor, or automation script later.

### ADR-002: Per-tenant connection set (three connections)
One `ConnectionManager` per tenant owns three distinct connections:

1. **TOM connection** (`Microsoft.AnalysisServices.Server`) — used exclusively for write operations: `RequestRefresh`, `SaveChanges`, schema reads. Not shared.
2. **ADOMD polling connection** (`AdomdConnection`) — used exclusively for background DMV reads (`DISCOVER_SESSIONS`, `DISCOVER_MEMORYUSAGE`). Opened/closed per poll cycle on the active tenant only. Others go dormant until user switches to them.
3. **Trace connection** (`Microsoft.AnalysisServices.Server`, separate instance) — opened when trace collection starts, owned by `TraceCollector`. Never used for commands.

Rationale: TOM `Server` is not thread-safe. Sharing a connection between polling and a blocking `SaveChanges()` call causes race conditions. The trace subscription must survive the duration of a refresh command and cannot share the command connection.

The manager also owns:
- MSAL `IPublicClientApplication` with a dedicated per-tenant token cache file
- `TenantContext` POCO holding connection state, `IsReadOnly` flag, and active model reference

### ADR-003: Route refresh by endpoint type
```
if (connectionString starts with "powerbi://")
    → PowerBiRefreshEngine (Enhanced Refresh REST API)
else
    → TomRefreshEngine (TOM RequestRefresh + SaveChanges)
```
Both implement `IRefreshEngine`. UI layer never knows which is in use. The Power BI path is preferred where available because it supports Cancel — SSMS's biggest gap.

**Cancellation on the TOM path:** `SaveChanges()` is synchronous and has no `CancellationToken`. Cancelling the wrapping `Task` does not cancel the server-side operation. The implementation must call `Server.CancelProcess(sessionId)` on a *separate* connection (the ADOMD polling connection is suitable). The TOM connection's blocking thread is then abandoned and a new TOM connection opened. Document this in `TomRefreshEngine`.

### ADR-004: SQLite for local history and trace archive
- One DB file per tenant, stored in platform app data dir
- `refresh_runs` table for refresh history
- `trace_events` table with rolling retention (keep last 30 days by default)
- Use `Microsoft.Data.Sqlite` not `System.Data.SQLite` (better .NET 8 support)
- **WAL mode is mandatory:** enable `PRAGMA journal_mode=WAL` at DB init. Trace event inserts and refresh history writes happen on different threads; WAL eliminates lock contention.
- **Batch trace inserts:** flush to SQLite every 500ms or 50 events, whichever comes first. Never insert per-event — trace bursts during large refreshes will saturate the writer.

### ADR-005: Adapt, don't take dependency on AsPartitionProcessing
Microsoft's `AsPartitionProcessing` sample is Apache 2.0 and valuable, but is structured as a console app with a SQL Server backing store. Copy `PartitionProcessor.cs` and related files into our repo, strip the SQL Server config DB, adapt logging to emit events on a `Channel<RefreshProgressEvent>`. Keep attribution in file headers.

### ADR-006: Trace collection uses classic `Trace` API first, xEvents as fallback
The `Microsoft.AnalysisServices` TOM library exposes `Server.Traces` for classic Profiler-style traces. This is simpler to wire up and AsTrace demonstrates the pattern. xEvents via `AsXEventSample` is lower overhead but more involved. Start with classic; switch to xEvents if we hit the multi-model overhead ceiling or if Power BI XMLA doesn't expose needed events on classic.

**Channel drainage:** `TraceCollector` always drains `Channel<TraceEvent>` to SQLite regardless of whether the trace view is visible. The UI subscribes to a secondary read stream from the store, not directly to the channel. This ensures events are never lost on tab switches and the channel never backs up.

### ADR-007: Grid-only partition view (no tree view)
Most tabular models in target use cases have tens of partitions per table, not hundreds. Grid layout works fine. Revisit if a user hits a model with 200+ partitions on a single table.

### ADR-008: Defer role impersonation
RLS testing was deprioritized in requirements gathering. Not in MVP. Can be added as a separate "Roles / RLS" view post-MVP.

### ADR-009: WPF, Windows-only, x64
**Decision: WPF.** The cross-platform escape hatch (Avalonia) is closed: `Microsoft.AnalysisServices.NetCore.retail.amd64` is a native AMD64 binary. The core library cannot run on macOS or Linux regardless of UI framework. Avalonia would add complexity with zero cross-platform benefit.

Both `.csproj` files must set `<PlatformTarget>x64</PlatformTarget>` explicitly. Without it, AnyCPU resolution silently picks the wrong native binary and produces `BadImageFormatException` at runtime.

### ADR-010: MVVM via CommunityToolkit.Mvvm
`[ObservableProperty]` and `[RelayCommand]` source generators handle the boilerplate. ReactiveUI's `IObservable<T>` is not adopted wholesale — if reactive composition is needed at a boundary, use `System.Reactive` surgically. The trace stream uses `Channel<T>` on the producer side; the ViewModel converts to `ObservableCollection` on the UI thread via the dispatcher.

### ADR-011: Read-only XMLA detection
Do not attempt a no-op write to probe permissions. Instead: after connecting, attempt the first write operation (refresh, partition command) and catch `AmoException` where the error code indicates insufficient permissions. On catch, set `TenantContext.IsReadOnly = true` and surface the state in the status bar. This is simpler than inspecting `DISCOVER_XML_METADATA` capabilities and covers the actual failure mode rather than a predicted one.

### ADR-012: Refresh profile template schema
Templates are stored per-model in SQLite as structured JSON (not opaque blobs). Defined schema:

```json
{
  "name": "Nightly facts",
  "scope": [
    { "table": "FactSales", "partitions": ["2024-01", "2024-02"] }
  ],
  "refreshType": "full | dataOnly | calculate | clearValues",
  "maxParallelism": 2
}
```

Users can export/import templates as `.json` files for version control. Schema is defined before the SQLite schema is written so the column type is `TEXT NOT NULL` (JSON), not a future migration.

### ADR-013: Add-connection UX
Two flows, both triggered by `Ctrl+N` or a `+` button at the bottom of the sidebar:

- **Power BI / Fabric:** Use MSAL's standard interactive browser popup (the same OAuth flow Microsoft 365 and Azure Portal use — no custom login UI). After auth, fetch workspace list via Power BI REST API and show a simple searchable list dialog (tenant name + workspace picker). Connection string derived as `powerbi://api.powerbi.com/v1.0/myorg/<workspace>`. This matches what users already know from connecting Excel or ADS to Power BI.
- **SSAS / AAS:** Simple dialog with a single connection string text field + "Test" button, styled to match the app's dark theme. No workspace picker — user pastes the full connection string.

No custom credential UI is designed or needed. Delegating auth entirely to MSAL's browser popup is both correct and familiar to the target user.

`TenantContext` must hold: `DisplayName`, `ConnectionString`, `EndpointType` (enum: `PowerBi | Ssas | Aas`), `IsReadOnly`, `TokenCacheFilePath`.

### ADR-014: Connection recovery
When a tenant's connection drops (network blip, token expiry, AS restart):
- The sidebar node shows an error indicator (red dot, same `--error` color token)
- A `Reconnect` button appears in the main content area instead of the normal view
- Auto-retry: exponential backoff, 3 attempts, then stop and wait for user action
- Token expiry is handled silently by MSAL's `AcquireTokenSilent` — only interactive re-auth is surfaced

### ADR-015: Partition processing states
Five states, not four. All must be represented in the partition cell:

| State | Border color | Background tint | Opacity |
|---|---|---|---|
| OK | `--border` | none | 100% |
| Stale | `--warn` | `--warn` dim | 55% |
| Queued | `--info` dim | none | 80% |
| Refreshing | `--info` | `--info` dim | 100% (pulse) |
| Failed | `--error` | `--error` dim | 100% |

"Queued" means triggered but not yet started by the server (waiting on `MaxParallelism` slot). "Refreshing" means the server has begun processing. TOM trace events (`ProgressReportBegin`) provide the transition signal.

### ADR-016: Minimum compatibility level
Minimum supported Analysis Services compatibility level: **1500** (SQL Server 2019 AS, AAS, Power BI XMLA). TOM 19.x does not guarantee all properties exist on lower-compatibility-level objects. If a user connects to a server running compatibility level < 1500, surface a clear error at connection time rather than a NullReferenceException later.

### ADR-017: MSAL client ID strategy

**Current choice: Power BI Desktop client ID (`7f67af8a-fedc-4b08-8b4e-37c4d127b6cf`)**

This is a Microsoft first-party client ID. Because it belongs to a pre-trusted Microsoft app, it works in every Entra tenant with no consent screen and no app registration required in the client tenant. This is the same approach used by Tabular Editor and Gerard Brueckl's Fabric VS Code extension.

**Trade-off:** Technically violates Microsoft's ToS — the ID belongs to Microsoft, not us. Microsoft has historically tolerated this in developer tooling but could revoke access without notice. For an internal consulting tool this risk is acceptable; for a distributed product it is not.

**Alternatives if this breaks or the tool is ever distributed:**

| Option | Friction | Risk |
|---|---|---|
| Power BI Desktop client ID *(current)* | None — no consent screen | Microsoft could revoke at any time |
| Own app registration (ID: `6dbbac90-f828-43f1-9d84-b39463049df5`) | Admin consent once per client tenant, or user consent if tenant allows it | None — fully above-board |
| Verified Publisher (MPN) | One-time Microsoft verification process; reduces consent friction for distributed apps | None |

To switch back to the own registration, update `EntraClientId` in `appsettings.json` to `6dbbac90-f828-43f1-9d84-b39463049df5`.

## Deferred / not in MVP

- Q5: Token cache on Linux — MSAL uses plaintext with opt-in to libsecret. Moot for MVP (Windows-only). Document if Linux support is ever added.
- Sessions view, memory breakdown, DAX query window, trend charts — post-MVP (see CLAUDE.md Milestone 6+).
- Role impersonation — ADR-008.

## Dependencies snapshot

```xml
<!-- TabularOps.Core.csproj -->
<PackageReference Include="Microsoft.AnalysisServices.NetCore.retail.amd64" Version="19.*" />
<PackageReference Include="Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64" Version="19.*" />
<PackageReference Include="Microsoft.Identity.Client" Version="4.*" />
<PackageReference Include="Microsoft.Identity.Client.Extensions.Msal" Version="4.*" />
<PackageReference Include="Microsoft.PowerBI.Api" Version="4.*" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
<PackageReference Include="System.Threading.Channels" Version="8.*" />

<!-- TabularOps.Desktop.csproj -->
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
```

Version pinning: resolve latest stable at project creation time.

## Font packaging

IBM Plex Sans and JetBrains Mono must be embedded as WPF resources (`Build Action: Resource`) in `TabularOps.Desktop` and registered in `App.xaml` via `FontFamily` resource dictionary entries. WPF does not fall back gracefully to system fonts — it silently substitutes and the UI looks wrong. Download and commit the font files before writing any XAML.
