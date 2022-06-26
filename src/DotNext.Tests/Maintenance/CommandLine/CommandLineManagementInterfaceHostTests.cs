using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace DotNext.Maintenance.CommandLine
{
    using Diagnostics;

    [ExcludeFromCodeCoverage]
    public sealed class CommandLineManagementInterfaceHostTests : Test
    {
        [Theory]
        [InlineData("probe readiness 00:00:01", "ok")]
        [InlineData("probe startup 00:00:01", "ok")]
        [InlineData("probe liveness 00:00:01", "fail")]
        [InlineData("gc collect 0", "")]
        [InlineData("gc loh-compaction-mode CompactOnce", "")]
        public static async Task DefaultCommandsAsync(string request, string response)
        {
            var unixDomainSocketPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            using var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services
                        .UseApplicationManagementInterface(unixDomainSocketPath)
                        .UseApplicationStatusProvider<TestStatusProvider>();
                })
                .Build();

            await host.StartAsync();

            var buffer = new byte[512];
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(unixDomainSocketPath));
                Equal(response, await ExecuteCommandAsync(socket, request, buffer));
                await socket.DisconnectAsync(true);
            }

            await host.StopAsync();
        }

        private static async Task<string> ExecuteCommandAsync(Socket socket, string command, byte[] buffer)
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(command + Environment.NewLine).AsMemory(), SocketFlags.None);

            var count = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);
            return Encoding.UTF8.GetString(buffer.AsSpan().Slice(0, count));
        }

        private sealed class TestStatusProvider : IApplicationStatusProvider
        {
            Task<bool> IApplicationStatusProvider.LivenessProbeAsync(CancellationToken token)
                => Task.FromResult(false);
        }
    }
}