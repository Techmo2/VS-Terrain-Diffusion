using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VSTerrainDiffusion.Core;

/// <summary>Tells a joining client what lapse rate this world's climate map is written against.</summary>
[ProtoContract]
public class ClimateScalePacket
{
    /// <summary>Matches <see cref="ClimateScale.LapseScale"/>.</summary>
    [ProtoMember(1)] public float LapseScale;

    /// <summary>False on a world the mod is not generating, where the game's own rate is correct.</summary>
    [ProtoMember(2)] public bool Active;
}

/// <summary>
/// Carries the climate lapse rate to clients.
///
/// The correction depends on how tall this world's blocks are in metres, which follows from the
/// model's native resolution, the world's scale and its vertical exaggeration - and, on a calibrated
/// world, from a peak elevation measured once and kept in the save. A client cannot work that out
/// for itself; it may not even have the model files. So the server states it outright.
///
/// A client that has the mod then tints blocks, reads temperatures and runs its weather on the same
/// numbers the server generated the world with. A client without it falls back to the game's own
/// lapse rate and will read high ground colder than it is - wrong, but only cosmetically and only
/// for that player, which is why the mod stays optional on the client.
///
/// In single player both sides share one process and one set of patches. The packet still goes
/// round the loopback, and installing twice is harmless.
/// </summary>
public class ClimateScaleSync : ModSystem
{
    private const string ChannelName = "vsterraindiffusion.climatescale";

    private ICoreClientAPI _capi;
    private IServerNetworkChannel _serverChannel;
    private bool _clientOwnsPatches;

    public override bool ShouldLoad(EnumAppSide side) => true;

    /// <summary>Before the terrain systems, so the channel exists by the time a world generates.</summary>
    public override double ExecuteOrder() => 0.05;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverChannel = api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<ClimateScalePacket>();

        api.Event.PlayerJoin += player => _serverChannel.SendPacket(Current(), player);
    }

    private static ClimateScalePacket Current() => new()
    {
        Active = ClimateScale.Installed,
        LapseScale = ClimateScale.LapseScale
    };

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<ClimateScalePacket>()
            .SetMessageHandler<ClimateScalePacket>(OnScaleReceived);
    }

    private void OnScaleReceived(ClimateScalePacket packet)
    {
        if (!packet.Active) return;

        ClimateScale.Install(_capi.Logger, packet.LapseScale);

        // In single player the server installed these and will take them down again; a client that
        // put them up for itself has to be the one to remove them when it leaves.
        _clientOwnsPatches = !_capi.IsSinglePlayer;
    }

    public override void Dispose()
    {
        if (!_clientOwnsPatches) return;

        _clientOwnsPatches = false;
        ClimateScale.Uninstall();
    }
}
