using System.Runtime.InteropServices;
using Vanara.PInvoke;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using static Vanara.PInvoke.CldApi;

namespace Clouder.CloudFilter;

/// <summary>
/// Creates and manipulates CfApi placeholders — files that appear in Explorer with
/// their real name, size and timestamps, but whose content is fetched from the cloud
/// only when something opens them.
///
/// Placeholders carry Clouder's internal item id ("{poolId}|{relativePath}") as their
/// file identity, which is what <see cref="SyncEngine"/> looks the file up by when
/// Windows requests hydration.
/// </summary>
public static class PlaceholderHelper
{
    /// <summary>
    /// Creates placeholders for <paramref name="items"/> inside <paramref name="baseDirectory"/>.
    /// Entries that already exist are skipped rather than failing the batch.
    /// Returns the number of placeholders actually created.
    /// </summary>
    public static int CreatePlaceholders(
        string baseDirectory, IReadOnlyList<(string RelativeName, CloudItem Item)> items)
    {
        if (items.Count == 0) return 0;

        Directory.CreateDirectory(baseDirectory);

        var createInfos = new CF_PLACEHOLDER_CREATE_INFO[items.Count];
        var identityHandles = new IntPtr[items.Count];

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var (relativeName, item) = items[i];
                var identityBytes = System.Text.Encoding.UTF8.GetBytes(item.Id);
                var identityPtr = Marshal.AllocHGlobal(identityBytes.Length);
                Marshal.Copy(identityBytes, 0, identityPtr, identityBytes.Length);
                identityHandles[i] = identityPtr;

                createInfos[i] = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = relativeName,
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = item.Type == CloudItemType.File ? item.Size : 0,
                        BasicInfo = new Kernel32.FILE_BASIC_INFO
                        {
                            FileAttributes = item.Type == CloudItemType.Folder
                                ? FileFlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY
                                : FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                            CreationTime = ToFileTime(item.CreatedAtUtc),
                            LastWriteTime = ToFileTime(item.ModifiedAtUtc),
                            LastAccessTime = ToFileTime(item.ModifiedAtUtc),
                            ChangeTime = ToFileTime(item.ModifiedAtUtc)
                        }
                    },
                    FileIdentity = identityPtr,
                    FileIdentityLength = (uint)identityBytes.Length,
                    // MARK_IN_SYNC: the placeholder matches the cloud copy, so Explorer
                    // shows the "in sync" overlay instead of treating it as pending.
                    Flags = item.Type == CloudItemType.Folder
                        ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                          | CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION
                        : CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                };
            }

            // A failure here is for the batch as a whole; individual entries report
            // their own status in Result, so one pre-existing file doesn't sink the rest.
            var hr = CfCreatePlaceholders(
                baseDirectory,
                createInfos,
                (uint)createInfos.Length,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                out _);

            if (hr.Failed)
                ClouderLog.Warn($"CfCreatePlaceholders reported {hr} for '{baseDirectory}'");

            int created = 0;
            for (int i = 0; i < createInfos.Length; i++)
            {
                if (createInfos[i].Result.Succeeded)
                {
                    created++;
                }
                else
                {
                    ClouderLog.Debug(
                        $"Placeholder for '{items[i].RelativeName}' not created: {createInfos[i].Result}");
                }
            }

            return created;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to create placeholders in '{baseDirectory}'", ex);
            return 0;
        }
        finally
        {
            foreach (var ptr in identityHandles)
            {
                if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }
    }

    /// <summary>
    /// Turns a real on-disk file into a placeholder owned by Clouder — used after an
    /// upload so the local file becomes a cloud-backed file that can later be dehydrated.
    /// </summary>
    public static bool ConvertToPlaceholder(string filePath, string itemId)
    {
        try
        {
            using var handle = OpenForWrite(filePath);
            if (handle == null) return false;

            var identityBytes = System.Text.Encoding.UTF8.GetBytes(itemId);
            var identityPtr = Marshal.AllocHGlobal(identityBytes.Length);
            try
            {
                Marshal.Copy(identityBytes, 0, identityPtr, identityBytes.Length);
                var hr = CfConvertToPlaceholder(
                    handle,
                    identityPtr,
                    (uint)identityBytes.Length,
                    CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC,
                    out _);

                if (hr.Failed)
                {
                    ClouderLog.Warn($"Could not convert '{filePath}' to a placeholder: {hr}");
                    return false;
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(identityPtr);
            }
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to convert '{filePath}' to a placeholder", ex);
            return false;
        }
    }

    /// <summary>Marks a placeholder as matching the cloud copy (the green check in Explorer).</summary>
    public static bool MarkInSync(string filePath)
    {
        try
        {
            using var handle = OpenForWrite(filePath);
            if (handle == null) return false;

            long usn = 0;
            var hr = CfSetInSyncState(
                handle,
                CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                ref usn);

            if (hr.Failed)
            {
                ClouderLog.Debug($"Could not mark '{filePath}' in sync: {hr}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to mark '{filePath}' in sync", ex);
            return false;
        }
    }

    /// <summary>
    /// Frees the local disk space used by a hydrated placeholder, leaving the file
    /// visible in Explorer but online-only. This is Explorer's "Free up space".
    /// </summary>
    public static bool Dehydrate(string filePath)
    {
        try
        {
            using var handle = OpenForWrite(filePath);
            if (handle == null) return false;

            var hr = CfDehydratePlaceholder(
                handle,
                0,
                -1, // whole file
                CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE,
                IntPtr.Zero);

            if (hr.Failed)
            {
                ClouderLog.Warn($"Could not free up space for '{filePath}': {hr}");
                return false;
            }

            ClouderLog.Info($"Freed local space for '{filePath}'");
            return true;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to dehydrate '{filePath}'", ex);
            return false;
        }
    }

    private static Kernel32.SafeHFILE? OpenForWrite(string filePath)
    {
        var handle = Kernel32.CreateFile(
            filePath,
            Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES | Kernel32.FileAccess.FILE_READ_ATTRIBUTES,
            FileShare.Read | FileShare.Write,
            null,
            FileMode.Open,
            FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            ClouderLog.Debug($"Could not open '{filePath}' for placeholder operations");
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(DateTime utc)
    {
        // DateTime.ToFileTimeUtc throws for values before 1601; fall back to "now"
        // rather than failing the whole placeholder batch over a bad timestamp.
        long ft;
        try { ft = utc.ToFileTimeUtc(); }
        catch { ft = DateTime.UtcNow.ToFileTimeUtc(); }

        return new System.Runtime.InteropServices.ComTypes.FILETIME
        {
            dwLowDateTime = (int)(ft & 0xFFFFFFFF),
            dwHighDateTime = (int)(ft >> 32)
        };
    }
}
