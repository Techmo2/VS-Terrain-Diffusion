using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VSTerrainDiffusion.Core;

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
    /// <summary>The network channel the packet travels on.</summary>
    private const string ChannelName = "vsterraindiffusion.climatescale";

    /// <summary>The client API, on the client side.</summary>
    private ICoreClientAPI _capi;

    /// <summary>The server's end of the channel.</summary>
    private IServerNetworkChannel _serverChannel;

    /// <summary>Whether this client installed the patches itself and so must remove them.</summary>
    private bool _clientOwnsPatches;

    /// <summary>Both sides: the server sends, the client receives.</summary>
    public override bool ShouldLoad(EnumAppSide side) => true;

    /// <summary>Before the terrain systems, so the channel exists by the time a world generates.</summary>
    public override double ExecuteOrder() => 0.05;

    /// <summary>Registers the channel and sends the current rate to each player as they join.</summary>
    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverChannel = api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<ClimateScalePacket>();

        api.Event.PlayerJoin += player => _serverChannel.SendPacket(Current(), player);
    }

    /// <summary>The packet describing the rate in force now.</summary>
    private static ClimateScalePacket Current() => new()
    {
        Active = ClimateScale.Installed,
        LapseScale = ClimateScale.LapseScale
    };

    /// <summary>Registers the channel and listens for the server's rate.</summary>
    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        api.Network
            .RegisterChannel(ChannelName)
            .RegisterMessageType<ClimateScalePacket>()
            .SetMessageHandler<ClimateScalePacket>(OnScaleReceived);
    }

    /// <summary>Installs the server's rate, unless the world is not the mod's.</summary>
    private void OnScaleReceived(ClimateScalePacket packet)
    {
        if (!packet.Active) return;

        ClimateScale.Install(_capi.Logger, packet.LapseScale);

        // In single player the server installed these and will take them down again; a client that
        // put them up for itself has to be the one to remove them when it leaves.
        _clientOwnsPatches = !_capi.IsSinglePlayer;
    }

    /// <summary>Removes the patches this client installed itself.</summary>
    public override void Dispose()
    {
        if (!_clientOwnsPatches) return;

        _clientOwnsPatches = false;
        ClimateScale.Uninstall();
    }
}
