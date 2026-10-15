namespace TabularOps.Core;

/// <summary>
/// Single source of truth for where TabularOps keeps its local state
/// (SQLite databases, MSAL token cache, preferences).
///
/// Default: %LOCALAPPDATA%\TabularOps.
/// Portable mode: if a file named <c>portable.flag</c> sits next to the executable,
/// everything is stored in a <c>data</c> folder beside it instead, so the app can
/// run from a USB stick or a jump box without leaving anything behind.
/// Note the MSAL cache is DPAPI-encrypted and only readable by the same Windows user.
/// </summary>
public static class AppPaths
{
    public const string PortableFlagFileName = "portable.flag";

    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableFlagFileName));

    public static string DataDirectory { get; } = IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TabularOps");

    public static string CacheDirectory      => Path.Combine(DataDirectory, "Cache");
    public static string RefreshHistoryDb    => Path.Combine(DataDirectory, "refresh-history.db");
    public static string PartitionCacheDb    => Path.Combine(DataDirectory, "partition-cache.db");
    public static string BackupHistoryDb     => Path.Combine(DataDirectory, "backup-history.db");
    public static string TraceDb             => Path.Combine(DataDirectory, "trace.db");
    public static string ThemePreferenceFile => Path.Combine(DataDirectory, "theme");

    /// <summary>
    /// One-time move of trace.db from %APPDATA% (Roaming), where earlier versions
    /// stored it, so a growing trace archive doesn't bloat roaming profiles.
    /// </summary>
    public static void MigrateLegacyLocations()
    {
        if (IsPortable) return;

        var legacyTraceDb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TabularOps", "trace.db");

        try
        {
            if (File.Exists(legacyTraceDb) && !File.Exists(TraceDb))
            {
                Directory.CreateDirectory(DataDirectory);
                File.Move(legacyTraceDb, TraceDb);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
