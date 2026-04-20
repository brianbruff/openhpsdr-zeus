namespace Nereus.Server;

// Hosted-service shim whose sole purpose is to force TxAudioIngest to be
// resolved at startup. TxAudioIngest's constructor subscribes to
// StreamingHub.MicPcmReceived; without an eager resolve no one ever asks
// the container for it and the first mic frames would land before the
// subscription attaches.
internal sealed class TxAudioIngestStartup : IHostedService
{
    public TxAudioIngestStartup(TxAudioIngest _) { /* resolve-only */ }
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
