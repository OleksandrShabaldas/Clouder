namespace Clouder.Providers.Mega;

public sealed class MegaSettings
{
    public string TokenStoragePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clouder", "tokens", "mega");

    /// <summary>
    /// Optional callback the UI sets before calling ConnectAccountAsync.
    /// Returns (email, password) from the user.
    /// </summary>
    public Func<Task<(string Email, string Password)>>? CredentialPrompt { get; set; }
}
