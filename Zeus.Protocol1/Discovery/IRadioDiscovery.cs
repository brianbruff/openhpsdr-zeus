namespace Nereus.Protocol1.Discovery;

public interface IRadioDiscovery
{
    Task<IReadOnlyList<DiscoveredRadio>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default);
}
