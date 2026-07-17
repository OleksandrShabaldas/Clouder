using System.Runtime.InteropServices;
using Vanara.PInvoke;
using Clouder.Core.Models;
using static Vanara.PInvoke.CldApi;

namespace Clouder.CloudFilter;

/// <summary>
/// Creates and manages CfApi placeholder files/folders in a sync root.
/// Placeholders appear as real files in Explorer but their content lives in the cloud.
/// </summary>
public static class PlaceholderHelper
{
    public static void CreatePlaceholders(string baseDirectory, IReadOnlyList<CloudItem> items)
    {
        if (items.Count == 0) return;

        var createInfos = new CF_PLACEHOLDER_CREATE_INFO[items.Count];

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var identityBytes = System.Text.Encoding.UTF8.GetBytes(item.RemoteId);
            var identityPtr = Marshal.AllocHGlobal(identityBytes.Length);
            Marshal.Copy(identityBytes, 0, identityPtr, identityBytes.Length);

            createInfos[i] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = item.Name,
                FsMetadata = new CF_FS_METADATA
                {
                    FileSize = item.Type == CloudItemType.File ? item.Size : 0,
                    BasicInfo = new Vanara.PInvoke.Kernel32.FILE_BASIC_INFO
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
                Flags = item.Type == CloudItemType.Folder
                    ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                      | CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION
                    : CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
            };
        }

        try
        {
            var hr = CfCreatePlaceholders(
                baseDirectory,
                createInfos,
                (uint)createInfos.Length,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                out _);

            hr.ThrowIfFailed("CfCreatePlaceholders failed");
        }
        finally
        {
            foreach (var info in createInfos)
            {
                if (info.FileIdentity != IntPtr.Zero)
                    Marshal.FreeHGlobal(info.FileIdentity);
            }
        }
    }

    private static System.Runtime.InteropServices.ComTypes.FILETIME ToFileTime(DateTime utc)
    {
        long ft = utc.ToFileTimeUtc();
        return new System.Runtime.InteropServices.ComTypes.FILETIME
        {
            dwLowDateTime = (int)(ft & 0xFFFFFFFF),
            dwHighDateTime = (int)(ft >> 32)
        };
    }

    public static void ConvertToPlaceholder(string filePath, string remoteId)
    {
        using var handle = Vanara.PInvoke.Kernel32.CreateFile(
            filePath,
            Vanara.PInvoke.Kernel32.FileAccess.FILE_WRITE_ATTRIBUTES,
            FileShare.Read,
            null,
            FileMode.Open,
            FileFlagsAndAttributes.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            throw new IOException($"Failed to open '{filePath}' for placeholder conversion.");

        var identityBytes = System.Text.Encoding.UTF8.GetBytes(remoteId);
        var identityPtr = Marshal.AllocHGlobal(identityBytes.Length);
        try
        {
            Marshal.Copy(identityBytes, 0, identityPtr, identityBytes.Length);
            CfConvertToPlaceholder(
                handle,
                identityPtr,
                (uint)identityBytes.Length,
                CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC,
                out _).ThrowIfFailed("CfConvertToPlaceholder failed");
        }
        finally
        {
            Marshal.FreeHGlobal(identityPtr);
        }
    }
}
