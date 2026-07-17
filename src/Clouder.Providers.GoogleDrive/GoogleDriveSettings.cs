namespace Clouder.Providers.GoogleDrive;

public sealed class GoogleDriveSettings
{
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
    public string TokenStoragePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder", "tokens", "google-drive");
}
