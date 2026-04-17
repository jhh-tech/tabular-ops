# Architecture Decisions

## Decided

### ADR-001: Core library has no UI dependencies
`TabularOps.Core` is a class library with no WPF/Avalonia references. All UI-facing data flows through plain POCOs and `Channel<T>` or `IObservable<T>` streams. This makes the library reusable from a CLI, Dagster sensor, or automation script later.

### ADR-002: Per-tenant `ConnectionManager` lifetime
One `ConnectionManager` per tenant (not per model, not per workspace). The manager owns:
- MSAL `IPublicClientApplication` with dedicated token cache file
- Active `Microsoft.AnalysisServices.Server` connection
- Background polling task for `DISCOVER_SESSIONS`, `DISCOVER_MEMORYUSAGE` on the active model

Only the currently-viewed tenant polls. Others go dormant until user switches to them.

### ADR-003: Route refresh by endpoint type
```
if (connectionString starts with "powerbi://")
    → PowerBiRefreshEngine (Enhanced Refresh REST API)
else
    → TomRefreshEngine (TOM RequestRefresh + SaveChanges)
```
Both implement `IRefreshEngine`. UI layer never knows which is in use. The Power BI path is preferred where available because it supports Cancel — SSMS's biggest gap.

### ADR-004: SQLite for local history and trace archive
- One DB file per tenant, stored in platform app data dir
- `refresh_runs` table for refresh history
- `trace_events` table with rolling retention (keep last 30 days by default)
- Use `Microsoft.Data.Sqlite` not `System.Data.SQLite` (better .NET 8 support)

### ADR-005: Adapt, don't take dependency on AsPartitionProcessing
Microsoft's `AsPartitionProcessing` sample is Apache 2.0 and valuable, but is structured as a console app with a SQL Server backing store. Copy `PartitionProcessor.cs` and related files into our repo, strip the SQL Server config DB, adapt logging to emit events on a `Channel<RefreshProgressEvent>`. Keep attribution in file headers.

### ADR-006: Trace collection uses classic `Trace` API first, xEvents as fallback
The `Microsoft.AnalysisServices` TOM library exposes `Server.Traces` for classic Profiler-style traces. This is simpler to wire up and AsTrace demonstrates the pattern. xEvents via `AsXEventSample` is lower overhead but more involved. Start with classic; switch to xEvents if we hit the multi-model overhead ceiling or if Power BI XMLA doesn't expose needed events on classic.

### ADR-007: Grid-only partition view (no tree view)
Most tabular models in target use cases have tens of partitions per table, not hundreds. Grid layout works fine. Revisit if a user hits a model with 200+ partitions on a single table.

### ADR-008: Defer role impersonation
RLS testing was deprioritized in requirements gathering. Not in MVP. Can be added as a separate "Roles / RLS" view post-MVP.

## Open questions

### Q1: WPF or Avalonia?

**WPF pros:**
- Native Windows, mature, well-documented
- TE2 is WinForms, TE3 is WPF — ecosystem familiarity
- Better debugging story
- Most .NET devs know it

**Avalonia pros:**
- Cross-platform (macOS, Linux)
- Modern XAML dialect
- Active development, good momentum

**Recommendation:** Start WPF unless cross-platform is a hard requirement. Windows-only is acceptable — SSMS and TE3 are Windows-only too, target users have Windows machines. Revisit if a future teammate on macOS needs to use the tool.

### Q2: MVVM framework

Options: CommunityToolkit.Mvvm (Microsoft, lightweight), ReactiveUI (powerful, steeper curve), or hand-rolled INotifyPropertyChanged.

**Recommendation:** CommunityToolkit.Mvvm. Handles `[ObservableProperty]`, `[RelayCommand]` via source generators. Good balance.

### Q3: How to present the `Microsoft.AnalysisServices` NuGet

The official package is `Microsoft.AnalysisServices.NetCore.retail.amd64` (AMD64 runtime, not AnyCPU). This constrains us to x64. Acceptable — ops tools don't need ARM64/x86.

### Q4: Where do refresh profile templates live?
"Nightly facts only" type presets need storage. Per-model? Per-tenant? Global?

**Tentative answer:** Per-model, stored in SQLite as JSON blobs. User can export/import as files.

### Q5: Token cache encryption on Linux
`Microsoft.Identity.Client.Extensions.Msal` uses DPAPI on Windows, Keychain on macOS, and plaintext on Linux (with opt-in to libsecret). Since MVP is Windows-only this is deferred, but document the limitation clearly if Linux support is ever added.

### Q6: What happens when XMLA endpoint is read-only?
Power BI Pro workspaces and Fabric workspaces without the right capacity give read-only XMLA. The app should detect this and:
- Allow browse / DAX query / trace
- Disable refresh/partition action buttons with a tooltip explaining why
- Show the read-only state in the status bar

## Dependencies snapshot

```xml
<!-- TabularOps.Core.csproj -->
<PackageReference Include="Microsoft.AnalysisServices.NetCore.retail.amd64" Version="19.*" />
<PackageReference Include="Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64" Version="19.*" />
<PackageReference Include="Microsoft.Identity.Client" Version="4.*" />
<PackageReference Include="Microsoft.Identity.Client.Extensions.Msal" Version="4.*" />
<PackageReference Include="Microsoft.PowerBI.Api" Version="4.*" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.*" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
<PackageReference Include="System.Threading.Channels" Version="8.*" />
```

Version pinning: let Claude Code resolve the latest stable at project creation time.
