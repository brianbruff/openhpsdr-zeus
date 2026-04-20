namespace Zeus.Protocol1;

public interface IRadioTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct);
}

public readonly record struct RadioEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}
