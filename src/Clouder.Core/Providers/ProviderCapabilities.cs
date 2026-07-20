namespace Clouder.Core.Providers;

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Upload = 1 << 0,
    Download = 1 << 1,
    Delete = 1 << 2,
    Move = 1 << 3,
    Rename = 1 << 4,
    CreateFolder = 1 << 5,
    VersionHistory = 1 << 6,
    RangeDownload = 1 << 7,
    ContentHashing = 1 << 8,
    /// <summary>Supports incremental change enumeration (vs. re-listing everything).</summary>
    IncrementalChanges = 1 << 9,

    BasicReadWrite = Upload | Download | Delete | CreateFolder,
    Full = BasicReadWrite | Move | Rename | VersionHistory | RangeDownload | ContentHashing | IncrementalChanges
}
