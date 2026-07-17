using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace Clouder.CloudFilter;

/// <summary>
/// Registers/unregisters Clouder sync roots with Windows.
/// Once registered, the sync root folder appears in Explorer's navigation pane.
/// </summary>
public static class SyncRootRegistrar
{
    private const string ProviderName = "Clouder";

    public static async Task RegisterAsync(string poolId, string poolName, string localPath)
    {
        Directory.CreateDirectory(localPath);

        var folder = await StorageFolder.GetFolderFromPathAsync(localPath);

        var syncRootInfo = new StorageProviderSyncRootInfo
        {
            Id = BuildSyncRootId(poolId),
            Path = folder,
            DisplayNameResource = poolName,
            IconResource = @"%SystemRoot%\system32\imageres.dll,-1043",
            Version = "1.0",
            RecycleBinUri = null,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.StreamingAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                | StorageProviderInSyncPolicy.DirectoryCreationTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            ProviderId = new Guid("C7A3F1E0-5B9D-4D7A-8E2F-3C1A9B0D6E4F"),
            Context = CryptographicBuffer.ConvertStringToBinary(poolId, BinaryStringEncoding.Utf8)
        };

        StorageProviderSyncRootManager.Register(syncRootInfo);

        // Mark the folder so Windows treats it as a cloud provider directory
        var dirInfo = new DirectoryInfo(localPath);
        dirInfo.Attributes |= System.IO.FileAttributes.ReadOnly;
    }

    public static void Unregister(string poolId)
    {
        try
        {
            StorageProviderSyncRootManager.Unregister(BuildSyncRootId(poolId));
        }
        catch (Exception)
        {
            // Sync root might already be unregistered
        }
    }

    public static bool IsRegistered(string poolId)
    {
        try
        {
            var syncRoot = StorageProviderSyncRootManager.GetSyncRootInformationForId(BuildSyncRootId(poolId));
            return syncRoot != null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildSyncRootId(string poolId) => $"{ProviderName}!{poolId}";
}
