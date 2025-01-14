﻿using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Rebus.ServiceProvider.Internals;

class RebusBackgroundService : BackgroundService
{
    private readonly RebusInitializer _rebusInitializer;

    public RebusBackgroundService(RebusInitializer rebusInitializer)
    {
        _rebusInitializer = rebusInitializer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (bus, _) = await _rebusInitializer._busAndEvents.Value;
        stoppingToken.Register(bus.Dispose);
    }
}