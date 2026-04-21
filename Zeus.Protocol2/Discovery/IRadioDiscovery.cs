namespace Zeus.Protocol2.Discovery;

public interface IRadioDiscovery
{
    Task<IReadOnlyList<DiscoveredRadio>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default);
}
