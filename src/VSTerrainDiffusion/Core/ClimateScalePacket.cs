using ProtoBuf;

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
