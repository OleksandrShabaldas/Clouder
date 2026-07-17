using System.Text.Json;
using Clouder.Core.Models;
using Clouder.Core.Storage;

namespace Clouder.Storage;

public sealed class ConfigService
{
    private const string ConfigKey = "clouder_config";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IMetadataStore _store;

    public ConfigService(IMetadataStore store) => _store = store;

    public async Task<ClouderConfig> LoadAsync(CancellationToken ct = default)
    {
        var json = await _store.GetSettingAsync(ConfigKey, ct);
        if (string.IsNullOrEmpty(json))
            return new ClouderConfig();

        return JsonSerializer.Deserialize<ClouderConfig>(json, JsonOpts) ?? new ClouderConfig();
    }

    public async Task SaveAsync(ClouderConfig config, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        await _store.SetSettingAsync(ConfigKey, json, ct);
    }
}
