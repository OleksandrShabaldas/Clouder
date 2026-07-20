using System.Collections.Concurrent;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Sync;

namespace Clouder.CloudFilter;

/// <summary>
/// Explorer-backed implementation of <see cref="IPlaceholderSink"/>: remote files become
/// CfApi placeholders (visible, correct size, content fetched on first open) and uploaded
/// files are converted to placeholders so they can later be dehydrated to free disk space.
/// Only pools whose sync root is actually connected are considered active.
/// </summary>
public sealed class CfPlaceholderSink : IPlaceholderSink
{
    private readonly ConcurrentDictionary<string, byte> _activePools = new(StringComparer.Ordinal);

    /// <summary>Marks a pool as having a live CfApi connection.</summary>
    public void Activate(string poolId) => _activePools[poolId] = 0;

    public void Deactivate(string poolId) => _activePools.TryRemove(poolId, out _);

    public bool IsActiveFor(string poolId) => _activePools.ContainsKey(poolId);

    public bool TryCreatePlaceholder(string poolId, string localFilePath, CloudItem item)
    {
        if (!IsActiveFor(poolId)) return false;

        try
        {
            var directory = Path.GetDirectoryName(localFilePath);
            if (string.IsNullOrEmpty(directory)) return false;

            var fileName = Path.GetFileName(localFilePath);

            // A stale local copy has to go first: CfCreatePlaceholders won't replace an
            // existing entry. The content is safe in the cloud, and this path only runs
            // when the local copy is known to be unmodified.
            if (File.Exists(localFilePath))
            {
                try { File.Delete(localFilePath); }
                catch (Exception ex)
                {
                    ClouderLog.Debug($"Could not replace '{localFilePath}' with a placeholder: {ex.Message}");
                    return false;
                }
            }

            var created = PlaceholderHelper.CreatePlaceholders(directory, [(fileName, item)]);
            return created > 0;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to create a placeholder for '{localFilePath}'", ex);
            return false;
        }
    }

    public void OnUploaded(string poolId, string localFilePath, string itemId)
    {
        if (!IsActiveFor(poolId)) return;

        try
        {
            if (!File.Exists(localFilePath)) return;

            // Convert first (a plain file isn't a placeholder yet); if it already is one,
            // conversion is a no-op and marking it in sync is what actually matters —
            // that's what clears Explorer's "Sync pending" status.
            bool converted = PlaceholderHelper.ConvertToPlaceholder(localFilePath, itemId);
            bool marked = PlaceholderHelper.MarkInSync(localFilePath);

            if (marked)
                ClouderLog.Debug($"'{Path.GetFileName(localFilePath)}' marked in sync"
                               + (converted ? " (converted to placeholder)" : ""));
            else
                ClouderLog.Warn($"Explorer still shows '{Path.GetFileName(localFilePath)}' as pending: "
                              + "the file could not be marked in sync.");
        }
        catch (Exception ex)
        {
            ClouderLog.Debug($"Could not mark '{localFilePath}' as cloud-backed: {ex.Message}");
        }
    }
}
