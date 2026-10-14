using Microsoft.Extensions.Hosting;

namespace Broker.NyaChat;

public sealed class NyaMemoryStartup(NyaMemoryService memory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = memory;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
