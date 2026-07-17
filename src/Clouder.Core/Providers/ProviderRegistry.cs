namespace Clouder.Core.Providers;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ICloudProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public ICloudProvider? GetProvider(string providerId) =>
        _providers.GetValueOrDefault(providerId);

    public IReadOnlyList<ICloudProvider> GetAllProviders() =>
        _providers.Values.ToList();

    public void Register(ICloudProvider provider) =>
        _providers[provider.ProviderId] = provider;
}
