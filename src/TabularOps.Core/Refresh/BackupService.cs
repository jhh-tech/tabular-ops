using TabularOps.Core.Connection;

namespace TabularOps.Core.Refresh;

/// <summary>
/// Backs up a tabular database to an ABF file via TOM and records the run in SQLite.
///
/// Supported on SSAS, AAS, and Power BI XMLA Premium/Fabric endpoints with
/// the Contribute permission or higher. Read-only XMLA endpoints will throw
/// OperationException — callers should surface the message to the user.
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
    /// Backs up <paramref name="databaseName"/> to <paramref name="filePath"/> (.abf).
    /// Records the run in SQLite regardless of outcome.
    /// </summary>
    /// <returns>The completed <see cref="BackupRun"/> record.</returns>
    public async Task<BackupRun> BackupAsync(
        string tenantId,
        string databaseName,
        string filePath,
        CancellationToken ct = default)
    {
        var runId = await _store.LogStartAsync(tenantId, databaseName, filePath, ct);

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
                    File            = filePath,
                    AllowOverwrite  = true,
                    ApplyCompression = true,
                };

                db.Backup(backupInfo);
            }, ct);

            long? fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : null;
            await _store.LogCompleteAsync(runId, succeeded: true, fileSize, errorMessage: null, ct);
        }
        catch (Exception ex)
        {
            await _store.LogCompleteAsync(runId, succeeded: false, null, ex.Message, ct);
            throw;
        }

        return (await _store.GetLastBackupAsync(tenantId, databaseName, ct))!;
    }
}
