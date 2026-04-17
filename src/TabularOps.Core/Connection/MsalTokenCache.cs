using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace TabularOps.Core.Connection;

/// <summary>
/// Wires up a per-tenant MSAL token cache backed by an encrypted file on disk.
/// On Windows: DPAPI encryption via Microsoft.Identity.Client.Extensions.Msal.
/// Call RegisterAsync once after creating the IPublicClientApplication.
/// </summary>
public static class MsalTokenCache
{
    private const string CacheFileName = "msal.cache";
    private const string KeychainService = "TabularOps";
    private const string KeychainAccount = "MSALCache";

    /// <summary>
    /// Registers a persistent, encrypted token cache on <paramref name="app"/>
    /// scoped to the given <paramref name="cacheDirectory"/>.
    /// </summary>
    public static async Task RegisterAsync(
        IPublicClientApplication app,
        string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);

        var storageProperties = new StorageCreationPropertiesBuilder(
                CacheFileName,
                cacheDirectory)
            // Windows: DPAPI encryption is applied automatically by MSAL Extensions
            // macOS: Keychain
            .WithMacKeyChain(KeychainService, KeychainAccount)
            // Linux: plaintext fallback — documented limitation, MVP is Windows-only
            .WithLinuxUnprotectedFile()
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
        cacheHelper.RegisterCache(app.UserTokenCache);
    }
}
