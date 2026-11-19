using System.Net;
using Tailshare.Core;

namespace Tailshare.Hub;

/// <summary>
/// Keeps a <see cref="HubServer"/> bound to this machine's Tailscale address and nothing else: it
/// re-reads Tailscale every <paramref name="recheck"/> and rebinds when the address changes or goes away.
/// </summary>
public static class HubHost
{
    public static async Task RunAsync(HubOptions options, int port, TimeSpan recheck, Action<string> log, CancellationToken stop)
    {
        HubServer? server = null;
        IPAddress? bound = null;

        async Task StopServerAsync()
        {
            if (server is null) return;
            await server.DisposeAsync();
            server = null;
            bound = null;
        }

        try
        {
            using var timer = new PeriodicTimer(recheck);
            do
            {
                var status = await options.Tailscale.GetStatusAsync(stop);
                if (!status.Connected)
                {
                    await StopServerAsync();
                }
                else if (server is null || !Equals(bound, status.SelfIp))
                {
                    await StopServerAsync();
                    var next = new HubServer(options);
                    try
                    {
                        await next.StartAsync(status.SelfIp!, port);
                        server = next;
                        bound = status.SelfIp;
                        log($"listening on port {next.Port}");
                    }
                    catch (Exception ex)
                    {
                        // Retried at the next check; saying why once per attempt is enough in the log.
                        await next.DisposeAsync();
                        log(ex is PortInUseException ? $"port {port} is in use" : "can't listen: " + ex.GetType().Name);
                    }
                }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await StopServerAsync();
        }
    }
}
