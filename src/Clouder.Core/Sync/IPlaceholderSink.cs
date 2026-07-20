using Clouder.Core.Models;

namespace Clouder.Core.Sync;

/// <summary>
/// Lets the sync engine make a remote file appear locally as an on-demand placeholder
/// instead of downloading its content. Implemented by the Explorer (CfApi) integration;
/// when no implementation is supplied, or it is inactive for a pool, the sync engine
/// falls back to downloading files normally.
/// </summary>
public interface IPlaceholderSink
{
    /// <summary>True when this pool is registered with Explorer and placeholders are usable.</summary>
    bool IsActiveFor(string poolId);

    /// <summary>
    /// Creates an on-demand placeholder at <paramref name="localFilePath"/> for a remote
    /// file. Returns false if the placeholder could not be created, in which case the
    /// caller should download the file instead.
    /// </summary>
    bool TryCreatePlaceholder(string poolId, string localFilePath, CloudItem item);

    /// <summary>
    /// Called after a local file has been uploaded, so it can be marked as backed by the
    /// cloud (enabling the "in sync" overlay and later "free up space").
    /// </summary>
    void OnUploaded(string poolId, string localFilePath, string itemId);
}
