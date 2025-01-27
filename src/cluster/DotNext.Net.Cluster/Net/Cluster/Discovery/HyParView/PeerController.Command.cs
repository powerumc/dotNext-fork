using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;

namespace DotNext.Net.Cluster.Discovery.HyParView;

using IRumorSender = Messaging.Gossip.IRumorSender;

public partial class PeerController
{
    private enum CommandType
    {
        Unknown = 0,

        Join,

        ForwardJoin,

        Disconnect,

        Neighbor,

        Shuffle,

        ShuffleReply,

        ForceShuffle,

        Broadcast,
    }

    // we use this struct as a placeholder for all HyParView commands to reduce GC pressure
    [StructLayout(LayoutKind.Auto)]
    private readonly struct Command
    {
        private readonly object? peersOrMessageTransport;

        internal CommandType Type { get; private init; }

        // null only if Type is ShuffleReply or ForceShuffle
        [DisallowNull]
        internal EndPoint? Sender { get; private init; }

        [DisallowNull]
        internal EndPoint? Origin { get; private init; }

        internal bool IsAliveOrHighPriority
        {
            get => TimeToLive != 0;
            private init => TimeToLive = value.ToInt32();
        }

        internal int TimeToLive { get; private init; }

        [DisallowNull]
        internal Func<PeerController, IRumorSender>? RumourTransport
        {
            get => peersOrMessageTransport as Func<PeerController, IRumorSender>;
            private init => peersOrMessageTransport = value;
        }

        internal IReadOnlyCollection<EndPoint> Peers
        {
            get => peersOrMessageTransport as IReadOnlyCollection<EndPoint> ?? Array.Empty<EndPoint>();
            private init => peersOrMessageTransport = value;
        }

        internal static Command Join(EndPoint joinedPeer) => new() { Type = CommandType.Join, Sender = joinedPeer };

        internal static Command ForwardJoin(EndPoint sender, EndPoint joinedPeer, int ttl) => new() { Type = CommandType.ForwardJoin, Sender = sender, Origin = joinedPeer, TimeToLive = ttl };

        internal static Command Neighbor(EndPoint sender, bool highPriority) => new() { Type = CommandType.Neighbor, Sender = sender, IsAliveOrHighPriority = highPriority };

        internal static Command Disconnect(EndPoint sender, bool isAlive) => new() { Type = CommandType.Disconnect, Sender = sender, IsAliveOrHighPriority = isAlive };

        internal static Command Shuffle(EndPoint sender, EndPoint origin, IReadOnlyCollection<EndPoint> peers, int ttl) => new() { Type = CommandType.Shuffle, Sender = sender, Origin = origin, Peers = peers, TimeToLive = ttl };

        internal static Command ForceShuffle() => new() { Type = CommandType.ForceShuffle };

        internal static Command ShuffleReply(IReadOnlyCollection<EndPoint> peers) => new() { Type = CommandType.ShuffleReply, Peers = peers };

        internal static Command Broadcast(Func<PeerController, IRumorSender> senderFactory) => new() { Type = CommandType.Broadcast, RumourTransport = senderFactory };
    }
}