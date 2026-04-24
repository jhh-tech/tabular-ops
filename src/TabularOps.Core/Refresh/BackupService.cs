using TabularOps.Core.Connection;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Backs up a tabular database via TOM and records the run in SQLite.
///
/// The server (SSAS, AAS, or Power BI XMLA) determines where the file is
/// stored based on its own configuration:
///   - SSAS: server's BackupDirectory property
///   - AAS / Power BI: Azure Blob Storage container configured on the server
///
/// If no storage is configured the server throws OperationException; callers
/// surface the message to the user.  The client only supplies a file name.
/// </summary>
public sealed class BackupService
{
    private readonly ConnectionManager _connectionManager;
    private readonly BackupStore _store;

    public BackupService(ConnectionManager connectionManager, BackupStore store)
    {
        _connectionManager = connectionManager;
        _store = store;
    }

    /// <summary>
    /// Backs up <paramref name="databaseName"/> using a timestamped file name.
    /// The server writes the file to its configured backup storage.
    /// </summary>
    /// <returns>The completed <see cref="BackupRun"/> record.</returns>
    public async Task<BackupRun> BackupAsync(
        string tenantId,
        string databaseName,
        CancellationToken ct = default)
    {
        // File name only — the server resolves the storage location.
        var fileName = $"{SanitizeFileName(databaseName)}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.abf";

        var runId = await _store.LogStartAsync(tenantId, databaseName, fileName, ct);

        try
        {
            var server = await _connectionManager.GetOrCreateCatalogServerAsync(
                tenantId, databaseName, ct);

            await Task.Run(() =>
            {
                var db = server.Databases
                    .Cast<Microsoft.AnalysisServices.Database>()
                    .FirstOrDefault(d => d.Name == databaseName)
                    ?? throw new InvalidOperationException(
                        $"Database '{databaseName}' not found on the server.");

                var backupInfo = new Microsoft.AnalysisServices.BackupInfo
                {
                    File             = fileName,
                    AllowOverwrite   = true,
                    ApplyCompression = true,
                };

                db.Backup(backupInfo);
            }, ct);

            await _store.LogCompleteAsync(runId, succeeded: true, errorMessage: null, ct);
        }
        catch (Exception ex)
        {
            await _store.LogCompleteAsync(runId, succeeded: false, ex.Message, ct);
            throw;
        }

        return (await _store.GetLastBackupAsync(tenantId, databaseName, ct))!;
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
