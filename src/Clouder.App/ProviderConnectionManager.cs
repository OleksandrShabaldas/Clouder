using System.Text.Json;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;
using Clouder.Email;
using Clouder.Providers.GoogleDrive;
using Clouder.Providers.Mega;

namespace Clouder_App;

public enum ConnectionState
{
    Unknown,
    Reconnecting,
    Connected,
    Failed,
    NeedsCredentials
}

/// <summary>
/// Persists provider credentials (encrypted with DPAPI) and rehydrates provider
/// objects on startup so accounts reconnect automatically without re-entering details.
/// </summary>
public sealed class ProviderConnectionManager
{
    private const string GoogleOAuthKey = "cred:google-oauth";
    private static string MegaCredKey(string accountId) => $"cred:mega:{accountId}";

    private readonly IMetadataStore _store;
    private readonly ProviderRegistry _providers;
    private readonly Dictionary<string, ConnectionState> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever any account's connection state changes.</summary>
    public event Action? StateChanged;

    public ProviderConnectionManager(IMetadataStore store, ProviderRegistry providers)
    {
        _store = store;
        _providers = providers;
    }

    public ConnectionState GetState(string accountId) =>
        _state.TryGetValue(accountId, out var s) ? s : ConnectionState.Unknown;

    public void SetState(string accountId, ConnectionState state)
    {
        _state[accountId] = state;
        StateChanged?.Invoke();
    }

    // ── Credential persistence (DPAPI-encrypted) ────────────────────────

    public async Task SaveGoogleCredentialsAsync(string clientId, string clientSecret)
    {
        var json = JsonSerializer.Serialize(new GoogleCreds { ClientId = clientId, ClientSecret = clientSecret });
        var enc = Convert.ToBase64String(CredentialProtector.Protect(json));
        await _store.SetSettingAsync(GoogleOAuthKey, enc);
    }

    public async Task<(string ClientId, string ClientSecret)?> LoadGoogleCredentialsAsync()
    {
        var enc = await _store.GetSettingAsync(GoogleOAuthKey);
        if (string.IsNullOrEmpty(enc)) return null;
        try
        {
            var json = CredentialProtector.Unprotect(Convert.FromBase64String(enc));
            var creds = JsonSerializer.Deserialize<GoogleCreds>(json);
            return creds == null ? null : (creds.ClientId, creds.ClientSecret);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to load Google credentials", ex);
            return null;
        }
    }

    public async Task SaveMegaCredentialsAsync(string accountId, string email, string password)
    {
        var json = JsonSerializer.Serialize(new MegaCreds { Email = email, Password = password });
        var enc = Convert.ToBase64String(CredentialProtector.Protect(json));
        await _store.SetSettingAsync(MegaCredKey(accountId), enc);
    }

    public async Task<(string Email, string Password)?> LoadMegaCredentialsAsync(string accountId)
    {
        var enc = await _store.GetSettingAsync(MegaCredKey(accountId));
        if (string.IsNullOrEmpty(enc)) return null;
        try
        {
            var json = CredentialProtector.Unprotect(Convert.FromBase64String(enc));
            var creds = JsonSerializer.Deserialize<MegaCreds>(json);
            return creds == null ? null : (creds.Email, creds.Password);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to load MEGA credentials", ex);
            return null;
        }
    }

    // ── Rehydrate providers on startup (fast, no network) ───────────────

    public async Task ReconnectAllAsync()
    {
        var accounts = await _store.GetAllAccountsAsync();

        // Google Drive — single provider serves all google accounts (shared OAuth app).
        bool hasGoogle = accounts.Any(a => a.ProviderId == "google-drive");
        if (hasGoogle && _providers.GetProvider("google-drive") == null)
        {
            var creds = await LoadGoogleCredentialsAsync();
            if (creds != null)
            {
                _providers.Register(new GoogleDriveProvider(new GoogleDriveSettings
                {
                    ClientId = creds.Value.ClientId,
                    ClientSecret = creds.Value.ClientSecret
                }));
                ClouderLog.Info("Google Drive provider rehydrated from saved credentials");
            }
            else
            {
                ClouderLog.Warn("Google accounts exist but no saved OAuth credentials — cannot auto-reconnect");
            }
        }

        // MEGA — single provider; session.json restores login lazily on first use.
        bool hasMega = accounts.Any(a => a.ProviderId == "mega");
        if (hasMega && _providers.GetProvider("mega") == null)
        {
            _providers.Register(new MegaProvider(new MegaSettings()));
            ClouderLog.Info("MEGA provider rehydrated");
        }

        // Initial optimistic state.
        foreach (var acc in accounts)
        {
            var provider = _providers.GetProvider(acc.ProviderId);
            if (provider == null)
                SetState(acc.AccountId, ConnectionState.NeedsCredentials);
            else
                SetState(acc.AccountId, ConnectionState.Reconnecting);
        }
    }

    // ── Background verification (may hit network / refresh tokens) ───────

    public async Task VerifyAllAsync()
    {
        var accounts = await _store.GetAllAccountsAsync();
        foreach (var acc in accounts)
            await VerifyAccountAsync(acc);
    }

    public async Task<bool> VerifyAccountAsync(ProviderAccount acc)
    {
        var provider = _providers.GetProvider(acc.ProviderId);
        if (provider == null)
        {
            SetState(acc.AccountId, ConnectionState.NeedsCredentials);
            return false;
        }

        SetState(acc.AccountId, ConnectionState.Reconnecting);
        try
        {
            var quota = await provider.GetQuotaAsync(acc.AccountId);
            acc.Quota = quota;
            await _store.UpsertAccountAsync(acc);
            SetState(acc.AccountId, ConnectionState.Connected);
            ClouderLog.Info($"Account verified: {acc.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Verification failed for '{acc.DisplayName}'", ex);

            // MEGA fallback: re-login with stored password if the session expired.
            if (acc.ProviderId == "mega" && provider is MegaProvider mega)
            {
                var creds = await LoadMegaCredentialsAsync(acc.AccountId);
                if (creds != null)
                {
                    try
                    {
                        await mega.RestoreAsync(acc.AccountId, creds.Value.Email, creds.Value.Password);
                        var quota = await provider.GetQuotaAsync(acc.AccountId);
                        acc.Quota = quota;
                        await _store.UpsertAccountAsync(acc);
                        SetState(acc.AccountId, ConnectionState.Connected);
                        ClouderLog.Info($"MEGA re-login succeeded: {acc.DisplayName}");
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        ClouderLog.Error($"MEGA re-login failed for '{acc.DisplayName}'", ex2);
                    }
                }
            }

            SetState(acc.AccountId, ConnectionState.Failed);
            return false;
        }
    }

    private sealed class GoogleCreds
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
    }

    private sealed class MegaCreds
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
