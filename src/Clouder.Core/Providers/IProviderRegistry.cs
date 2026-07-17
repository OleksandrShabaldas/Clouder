namespace Clouder.Core.Providers;

public interface IProviderRegistry
{
    ICloudProvider? GetProvider(string providerId);
    IReadOnlyList<ICloudProvider> GetAllProviders();
    void Register(ICloudProvider provider);
}
