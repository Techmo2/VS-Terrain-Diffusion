namespace VSTerrainDiffusion.Core;

/// <summary>
/// How the model's metres, degrees and millimetres become a Vintage Story world.
///
/// The defaults reproduce the Terrain Diffusion Minecraft mod's geometry: a block is exactly as
/// tall as it is wide, so the landscape is at true scale in every direction and a 2 000 m massif
/// really is 2 000 m of climbing. That only works in a world with the height to hold it, which is
/// why the mod asks for a tall world rather than stretching the terrain to fit a short one.
/// </summary>
public class WorldGenConfig
{
    /// <summary>
    /// "isotropic" makes vertical scale match horizontal, the way the Minecraft mod does it.
    /// "manual" uses <see cref="MetersPerBlockVertical"/>. "auto" measures the region's peaks once
    /// per world and stretches terrain to fill the world height, which suits short worlds at the
    /// cost of exaggerated relief.
    /// </summary>
    public string HeightMode { get; set; } = "isotropic";

    /// <summary>
    /// manual: metres of elevation per block of height. Zero falls back to isotropic.
    /// </summary>
    public float MetersPerBlockVertical { get; set; }

    /// <summary>
    /// auto: how much of the space between sea level and the world ceiling the region's tall
    /// peaks should occupy. Leave a little room, or the summits flatten against the ceiling.
    /// </summary>
    public float TargetPeakFillFraction { get; set; } = 0.92f;

    /// <summary>
    /// auto: which elevation quantile counts as a "tall peak". 0.995 means the top half percent of
    /// the surveyed area reaches the ceiling; lowering it makes the whole landscape taller and
    /// clips more summits.
    /// </summary>
    public float PeakQuantile { get; set; } = 0.995f;

    /// <summary>
    /// auto: half-width, in blocks, of the area surveyed around spawn. This should cover the part
    /// of the world you expect to explore: surveying a whole continent lets a distant mountain
    /// range decide the scale and leaves your own surroundings flat.
    /// </summary>
    public int CalibrationRadiusBlocks { get; set; } = 4096;

    /// <summary>
    /// auto: how many full-detail probes to run on the tallest surveyed cells. The survey itself
    /// only sees terrain averaged over several kilometres, so peaks need measuring at full
    /// resolution. Each probe costs about as much as one terrain tile, once per world. Zero skips
    /// probing and falls back to <see cref="ReliefFactor"/>.
    /// </summary>
    public int CalibrationProbes { get; set; } = 8;

    /// <summary>
    /// auto: assumed ratio of true peak height to the coarse survey's value, used when probing is
    /// disabled or fails.
    /// </summary>
    public float ReliefFactor { get; set; } = 1.6f;

    /// <summary>auto: bounds on the vertical exaggeration calibration is allowed to choose.</summary>
    public float MinAutoExaggeration { get; set; } = 1f;

    /// <summary>
    /// auto: upper bound on the exaggeration calibration may choose; see <see cref="MinAutoExaggeration"/>.
    /// </summary>
    public float MaxAutoExaggeration { get; set; } = 20f;

    /// <summary>
    /// Fraction of the available height that is mapped perfectly linearly. Above the knee the
    /// curve bends over so that arbitrarily tall model peaks still fit under the ceiling; the
    /// closer this is to 1 the more faithful the summits and the harder they clip.
    /// </summary>
    public float LinearKneeFraction { get; set; } = 0.85f;

    /// <summary>Fraction of the space below sea level that the deepest ocean reaches.</summary>
    public float OceanDepthFraction { get; set; } = 0.9f;

    /// <summary>
    /// Extra block rows per unit of the model's own height (the square root of metres) at the
    /// waterline, on land and sea floor alike. The model squares its heights on the way out, which
    /// crushes the first few metres of every coast into one wide flat row; rows spaced in its own
    /// units give coasts an even slope instead. 0 turns it off. Recorded in a world when it is
    /// created; existing worlds keep what they were generated with.
    /// </summary>
    public float ShoreDetail { get; set; } = 1f;

    /// <summary>
    /// How far the shore detail reaches, in square-root-of-metres: it falls by a factor of e every
    /// this many units, so at 3 it is mostly gone by 60 m. High ground and deep sea end up moved
    /// by ShoreDetail x ShoreFade blocks (3 at the defaults), not reshaped.
    /// </summary>
    public float ShoreFade { get; set; } = 3f;

    /// <summary>
    /// Multiplies the Perlin detail added to sloped ground. The model resolves features down to one
    /// native pixel, so hillsides need roughness of their own; raise for craggier slopes.
    /// </summary>
    public float SlopeDetailStrength { get; set; } = 1f;

    /// <summary>
    /// What the game's 0-255 rainfall byte is built from. "moisture" uses the model's aridity —
    /// precipitation measured against how much the climate can evaporate, discounted for a dry
    /// season — which is what actually decides whether ground is bare, and stops warm-but-rainy
    /// and cold-but-dry places from being read as the same. "precipitation" uses annual millimetres
    /// alone.
    /// </summary>
    public string RainfallBasis { get; set; } = "moisture";

    /// <summary>
    /// moisture: the tree-moisture value that maps to the middle of the rainfall scale.
    /// </summary>
    public float MoistureMedian { get; set; } = 0.62f;

    /// <summary>moisture: spread of log tree-moisture across the model's land.</summary>
    public float MoistureSpread { get; set; } = 1.0f;

    /// <summary>
    /// precipitation: annual millimetres that map to the middle of the rainfall scale.
    /// </summary>
    public float RainfallMedianMm { get; set; } = 540f;

    /// <summary>precipitation: spread of the model's log precipitation over land.</summary>
    public float RainfallSpread { get; set; } = 0.8f;

    /// <summary>
    /// Added to the final rainfall as a fraction of full scale, so the model's own answer stands
    /// unless this says otherwise.
    ///
    /// The climate map cancels Vintage Story's own "higher ground is wetter" bonus, because the
    /// model already models orography properly, and this used to default to 0.05 to put that
    /// bonus's average back - vanilla's thresholds were tuned with it present. In practice it read
    /// as a world that was too lush everywhere, so the compensation is now opt-in: raise it for a
    /// wetter world, lower it for a drier one.
    ///
    /// It is an *additive* trim, which is what makes it the right knob for "a bit too wet": it
    /// moves every column by the same amount. <see cref="MoistureMedian"/> shifts a log-normal and
    /// so bites hardest in places that are already dry, which turns arid regions into desert.
    /// </summary>
    public float RainfallBias { get; set; }

    /// <summary>Degrees Celsius added to every model temperature, for a warmer or colder world.</summary>
    public float TemperatureOffsetC { get; set; }

    /// <summary>
    /// How far vanilla's forest noise opens up the model's woods into fields and glens, 0 to 1.
    /// Vanilla's map is patchy noise - about a third of it open ground, in patches roughly a
    /// kilometre across - and this thins the trees there by that fraction, leaving the model's
    /// cover alone where vanilla's is forest. 0 turns it off; 1 leaves the open patches bare.
    /// </summary>
    public float ForestClearings { get; set; } = 0.8f;

    /// <summary>The same for shrubs, from vanilla's own shrub noise.</summary>
    public float ShrubClearings { get; set; } = 0.8f;

    /// <summary>
    /// Swing temperature through the year using the model's temperature seasonality (BIO4) rather
    /// than from latitude alone, which is all vanilla has to go on. Continental interiors then get
    /// hard winters and hot summers while maritime and tropical climates at the same latitude stay
    /// even. Latitude still shows through, because a polar climate is a strongly seasonal one.
    /// </summary>
    public bool SeasonalTemperature { get; set; } = true;

    /// <summary>Multiplies the modelled seasonal temperature swing. Zero gives a world with no seasons.</summary>
    public float SeasonalTemperatureStrength { get; set; } = 1f;

    /// <summary>
    /// Swing rainfall through the year using the model's precipitation seasonality, so monsoon
    /// climates get a real wet and dry season rather than drizzling evenly all year.
    /// </summary>
    public bool SeasonalPrecipitation { get; set; } = true;

    /// <summary>Multiplies the modelled wet/dry season contrast.</summary>
    public float SeasonalPrecipitationStrength { get; set; } = 1f;

    /// <summary>
    /// Least share of a place's annual rainfall the dry season is allowed to fall to.
    ///
    /// A safety rail rather than a tuning knob: at the default strength it only binds above a
    /// coefficient of variation of roughly 100%. There the driest weeks would otherwise see no rain
    /// at all, and Vintage Story's farmland integrates precipitation into a moisture level that
    /// stalls crops below 10%, so an absolute drought parks every unirrigated field for months.
    /// Zero restores the undamped swing.
    /// </summary>
    public float SeasonalPrecipitationFloor { get; set; } = 0.4f;

    /// <summary>
    /// How long a translocator may wait for a chunk peek that never came back before starting its
    /// search again, in seconds. 0 disables the watchdog. Held to at least
    /// <see cref="TranslocatorPeekPauseSeconds"/> plus a minute, so it cannot fire while a peek is
    /// still waiting its turn, and each restart waits longer than the last.
    ///
    /// The server drops a queued peek if it cannot pause every worldgen thread within 3.6 seconds,
    /// and a worldgen thread waiting on the model can take longer than that. The translocator has
    /// already cleared the flag that would make it try again, so without this it stays on "Warping
    /// spacetime..." until its chunk is unloaded and read back from disk.
    /// </summary>
    public int TranslocatorSearchTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// How long the server may wait for world generation to pause before a chunk peek, in seconds.
    /// 0 keeps Vintage Story's own 3.6.
    ///
    /// Worldgen threads only park between chunk columns, and a column here waits on the model
    /// behind a single inference gate, so several queued threads routinely take longer than 3.6 s
    /// to come to rest. The server then throws the peek away - after the terrain was generated -
    /// and the translocator has to start over. Nothing can cut a column short, so the only
    /// remaining option is to wait for it.
    /// </summary>
    public int TranslocatorPeekPauseSeconds { get; set; } = 30;

    /// <summary>
    /// Caps how far a translocator looks for its partner, in blocks. 0 keeps Vintage Story's own
    /// 8000.
    ///
    /// Each attempt generates nine chunk columns and throws them away, four times a second, until
    /// it finds a ruin. A smaller ring keeps those attempts inside terrain that is already
    /// generated and still cached. It also makes translocator hops shorter, and it changes which
    /// translocator links to which, which is why it is off by default. Links already made are kept.
    /// </summary>
    public int TranslocatorMaxRangeBlocks { get; set; }

    /// <summary>
    /// Only consider translocator search candidates whose terrain is already in memory or the tile
    /// database, so the search never runs the model. Each other candidate is passed over at no cost
    /// and another drawn on the next tick. For a server with pregenerated terrain: a translocator
    /// only links to a ruin inside the stored area, and with none stored in its 400 to 8000 block
    /// ring it searches forever. Off by default.
    /// </summary>
    public bool TranslocatorStoredTerrainOnly { get; set; }

    /// <summary>
    /// Swing the year the opposite way south of the equator.
    ///
    /// On by default, and not really optional: Vintage Story already does this. Its calendar takes
    /// the hemisphere from the sign of the same latitude this mod reads, and
    /// <c>GetSeasonRel</c> shifts the year half a turn for the southern one - which is what decides
    /// foliage, crop growth and everything else that asks the calendar what season it is. Leaving
    /// this off does not give the world one hemisphere; it gives it a southern hemisphere whose
    /// leaves fall in the spring, because only the temperature curve stayed northern.
    ///
    /// There is no world it is right to turn off. The game's own hemisphere does not depend on this
    /// mod's latitude bands, so even at <see cref="LatitudeStrength"/> 0 the calendar still flips
    /// and this should follow it; on a "Patchy" world the game reports one hemisphere everywhere
    /// and the setting does nothing either way. It stays a switch only because someone may prefer
    /// one long season to a world that is half out of step with their own.
    /// </summary>
    public bool SeasonHemispheres { get; set; } = true;

    /// <summary>
    /// Stretch the altitude bands of vanilla's surface block layers to match the terrain height, so
    /// that hills which are only tall because of vertical exaggeration are not surfaced as bare
    /// alpine gravel. Has no effect at isotropic scale, where the bands already line up.
    /// </summary>
    public bool RescaleBlockLayerAltitudes { get; set; } = true;

    /// <summary>
    /// Leave slopes too steep to hold soil as bare rock. The threshold comes from the model's own
    /// moisture, because roots are what keep a hillside from shedding its soil.
    /// </summary>
    public bool BareSlopeRock { get; set; } = true;

    /// <summary>
    /// Cap ground whose warmest month never rises above freezing with glacier ice, so ice fields
    /// look permanent rather than like a winter that has not melted yet.
    /// </summary>
    public bool GlacierIce { get; set; } = true;

    /// <summary>
    /// Set the coarse model's climate to what story structures force (forceTemperature, forceRain),
    /// faded into the surroundings, instead of leaving vanilla to overwrite the climate map in a
    /// disc; and, with <see cref="StructureBasinDepth"/>, level the ground where they ask for flat
    /// land. Changes the terrain around those structures, and the coarse model's terrain for some
    /// way beyond them.
    /// </summary>
    public bool StructureConditioning { get; set; } = true;

    /// <summary>
    /// Experimental, with <see cref="StructureConditioning"/>: also flatten the coarse relief where a
    /// structure asks for a flat landform.
    /// </summary>
    public bool StructureFlatten { get; set; }

    /// <summary>
    /// Experimental, with <see cref="StructureConditioning"/>: how far the elevation the coarse model is
    /// asked for is pulled towards sea level where a structure asks for flat ground (0 to 1, 0 off) -
    /// the river basin's warp, so the model draws lowland, which it draws level. Structures vanilla
    /// places at sea level are placed on the ground there instead.
    /// </summary>
    public float StructureBasinDepth { get; set; } = 1f;

    /// <summary>
    /// Experimental: the elevation conditioning's noise level inside those sites (lower binds tighter;
    /// 0 leaves the world's).
    /// </summary>
    public float StructureElevationNoise { get; set; } = 0.02f;

    /// <summary>
    /// Which way the world's ocean map and the model's terrain are made to agree.
    ///
    /// "input" conditions the model on the ocean map, so the world's "Land cover" and "Ocean scale"
    /// settings - and any ocean map another mod installs in their place - decide where the sea is,
    /// and the map itself is left untouched for everything else that reads it.
    ///
    /// "output" is the reverse: the model invents its own continents and the ocean map is rewritten
    /// to match them. That gives coastlines drawn entirely from real-world terrain, at the price of
    /// ignoring the world's settings and overwriting any other mod's ocean map.
    /// </summary>
    public string OceanMap { get; set; } = "input";

    /// <summary>
    /// input: how completely the ocean map overrides the model's own sense of where land belongs,
    /// from 0 (ignored) to 1. Below 1 the map biases the coastline rather than setting it, which
    /// keeps more of the model's structure at the cost of honouring the world settings less exactly.
    /// </summary>
    public float LandmaskStrength { get; set; } = 1f;

    /// <summary>
    /// input: how much noise the model is told the landmask carries, which is how it decides
    /// whether the mask is a hint or an instruction. <em>Lower binds it more tightly</em> — the
    /// value is mixed as <c>cos(atan(n))</c> conditioning against <c>sin(atan(n))</c> noise, so it
    /// runs the opposite way to its name in the model's own config, where it is called
    /// <c>cond_snr</c>.
    ///
    /// The model ships 0.5, at which it reproduces the ocean map over about 88% of the world;
    /// 0.1 gets that to 95% and is the default here. Below that the gain is under 2% and the
    /// conditioning starts flattening the land it does keep. Zero uses the model's own value.
    /// </summary>
    public float LandmaskNoiseLevel { get; set; } = 0.1f;

    /// <summary>
    /// How much of the world's "Global temperature" and "Global precipitation" settings is built
    /// into the climate the model is conditioned on, from 0 to 1. The rest is applied to the
    /// model's output afterwards, so the world reads the same either way; what changes is whether
    /// the model knew. At 1 an arid world is drawn as an arid world, with the drainage, vegetation
    /// and soils to match; at 0 it is a temperate world with its rainfall scaled down on the way
    /// out, which is what this mod used to do and what vanilla does.
    /// </summary>
    public float GlobalClimateStrength { get; set; } = 1f;

    /// <summary>
    /// How much noise the model is told the shifted climate carries, on the same inverted scale as
    /// <see cref="LandmaskNoiseLevel"/>: <em>lower binds it more tightly</em>. Only consulted when
    /// one of the two settings is off its default, so an ordinary world keeps the model's own
    /// climate character. Zero uses the model's value for every world.
    /// </summary>
    public float ClimateNoiseLevel { get; set; }

    /// <summary>
    /// How much of a north-south climate gradient the world gets, from 0 to 1.
    ///
    /// The model's climate is a real climatology with continents, maritime coasts, rain shadows and
    /// altitude in it, but nothing in it knows which way is north: left alone it puts the cold
    /// places wherever its noise put them, and the world's <c>polarEquatorDistance</c> means
    /// nothing. At 1 the equator, the subtropical deserts, the mid-latitude storm track and the ice
    /// caps are conditioned into the model at the latitudes the game says they belong, and
    /// everything the model knows about coasts and mountains happens <em>within</em> those bands.
    /// At 0 the world is unrooted, which is what the mod did before 0.5.
    ///
    /// Which block is at which latitude is the game's answer, not this mod's, so day length,
    /// midnight sun and the hemispheres all agree with the snow line for free.
    /// </summary>
    public float LatitudeStrength { get; set; } = 1f;

    /// <summary>
    /// Honour the world's "Starting climate" setting by placing the spawn on land near sea level
    /// whose temperature there, on the day a new world starts, falls in the chosen band. Vanilla implements that setting by shifting its own
    /// climate map, which cannot be done to a model that predicts a specific world, so the player
    /// moves instead. Turn off to spawn on the nearest land whatever its climate.
    /// </summary>
    public bool StartingClimateSearch { get; set; } = true;

    /// <summary>
    /// How far from the middle of the map the starting climate search may look, in blocks. The
    /// search stops as soon as it finds matching land, so this is only the point at which it gives
    /// up and takes the closest temperature it saw. Surveying the full radius costs a few seconds
    /// once per world.
    /// </summary>
    public int StartingClimateSearchRadiusBlocks { get; set; } = 65536;

    /// <summary>
    /// How much more reluctant the search is to move the spawn north or south than east or west.
    /// Distance along Z decides day length in Vintage Story, and past the world's polar distance it
    /// buys midnight sun and polar night; distance along X costs nothing at all. At 2 the search
    /// will go twice as far east for the same climate before it heads for a pole.
    /// </summary>
    public float StartingClimateNorthSouthCost { get; set; } = 2f;

    /// <summary>
    /// How high above sea level the spawn may be, in blocks. The spawn is always on land near sea
    /// level, and its starting climate is judged by the temperature there.
    /// </summary>
    public int SpawnMaxBlocksAboveSea { get; set; } = 10;

    /// <summary>
    /// Overrides how much of the climate the model drives: "full" for model temperature, rainfall
    /// and vegetation with no latitude bands, "off" to leave Vintage Story's climate alone. Empty
    /// uses the world's own setting, which also defaults to full.
    /// </summary>
    public string ClimateMode { get; set; } = "";

    /// <summary>
    /// Overrides the world's "Resolution" setting. Zero uses the world setting. Values
    /// above 6 are only reachable from here.
    /// </summary>
    public int ScaleOverride { get; set; }

    /// <summary>
    /// Overrides the world's "Vertical exaggeration" setting. Zero uses the world setting. In auto
    /// mode this multiplies the calibrated height rather than setting it outright.
    /// </summary>
    public float VerticalExaggerationOverride { get; set; }

    /// <summary>
    /// Clamps every world-shaping setting to its supported range and normalises the text ones.
    /// </summary>
    internal void Sanitize()
    {
        HeightMode = (HeightMode ?? "isotropic").Trim().ToLowerInvariant();
        if (HeightMode is not ("auto" or "manual")) HeightMode = "isotropic";

        TargetPeakFillFraction = Clamp(TargetPeakFillFraction, 0.2f, 1f, 0.92f);
        PeakQuantile = Clamp(PeakQuantile, 0.5f, 1f, 0.995f);
        CalibrationRadiusBlocks = (int)Clamp(CalibrationRadiusBlocks, 512f, 4_000_000f, 4096f);
        CalibrationProbes = (int)Clamp(CalibrationProbes, 0f, 64f, 8f);
        ReliefFactor = Clamp(ReliefFactor, 1f, 5f, 1.6f);

        MinAutoExaggeration = Clamp(MinAutoExaggeration, 0.05f, 100f, 1f);
        MaxAutoExaggeration = Clamp(MaxAutoExaggeration, 0.05f, 100f, 20f);
        if (MaxAutoExaggeration < MinAutoExaggeration) MaxAutoExaggeration = MinAutoExaggeration;

        if (MetersPerBlockVertical < 0f) MetersPerBlockVertical = 0f;

        LinearKneeFraction = Clamp(LinearKneeFraction, 0.1f, 0.99f, 0.85f);
        OceanDepthFraction = Clamp(OceanDepthFraction, 0.05f, 1f, 0.9f);
        SlopeDetailStrength = Clamp(SlopeDetailStrength, 0f, 8f, 1f);
        ShoreDetail = Clamp(ShoreDetail, 0f, 8f, 1f);
        ShoreFade = Clamp(ShoreFade, 0.5f, 20f, 3f);

        RainfallBasis = (RainfallBasis ?? "moisture").Trim().ToLowerInvariant();
        if (RainfallBasis != "precipitation") RainfallBasis = "moisture";
        MoistureMedian = Clamp(MoistureMedian, 0.01f, 100f, 0.62f);
        MoistureSpread = Clamp(MoistureSpread, 0.1f, 4f, 1f);
        RainfallMedianMm = Clamp(RainfallMedianMm, 10f, 10000f, 540f);
        RainfallSpread = Clamp(RainfallSpread, 0.1f, 4f, 0.8f);
        RainfallBias = Clamp(RainfallBias, -1f, 1f, 0f);
        TemperatureOffsetC = Clamp(TemperatureOffsetC, -40f, 40f, 0f);

        ForestClearings = Clamp(ForestClearings, 0f, 1f, 0.8f);
        ShrubClearings = Clamp(ShrubClearings, 0f, 1f, 0.8f);

        SeasonalTemperatureStrength = Clamp(SeasonalTemperatureStrength, 0f, 4f, 1f);
        SeasonalPrecipitationStrength = Clamp(SeasonalPrecipitationStrength, 0f, 4f, 1f);
        SeasonalPrecipitationFloor = Clamp(SeasonalPrecipitationFloor, 0f, 1f, 0.4f);
        TranslocatorSearchTimeoutSeconds = (int)Clamp(TranslocatorSearchTimeoutSeconds, 0f, 1800f, 120f);
        TranslocatorPeekPauseSeconds = (int)Clamp(TranslocatorPeekPauseSeconds, 0f, 120f, 30f);
        TranslocatorMaxRangeBlocks = (int)Clamp(TranslocatorMaxRangeBlocks, 0f, 8000f, 0f);

        OceanMap = (OceanMap ?? "input").Trim().ToLowerInvariant();
        if (OceanMap != "output") OceanMap = "input";
        LandmaskStrength = Clamp(LandmaskStrength, 0f, 1f, 1f);
        if (LandmaskNoiseLevel != 0f) LandmaskNoiseLevel = Clamp(LandmaskNoiseLevel, 0.01f, 8f, 0.1f);
        GlobalClimateStrength = Clamp(GlobalClimateStrength, 0f, 1f, 1f);
        LatitudeStrength = Clamp(LatitudeStrength, 0f, 1f, 1f);
        if (ClimateNoiseLevel != 0f) ClimateNoiseLevel = Clamp(ClimateNoiseLevel, 0.01f, 8f, 0f);

        StartingClimateSearchRadiusBlocks = (int)Clamp(StartingClimateSearchRadiusBlocks, 512f, 4_000_000f, 65536f);
        StartingClimateNorthSouthCost = Clamp(StartingClimateNorthSouthCost, 1f, 100f, 2f);
        SpawnMaxBlocksAboveSea = (int)Clamp(SpawnMaxBlocksAboveSea, 1f, 256f, 10f);

        ClimateMode = (ClimateMode ?? "").Trim().ToLowerInvariant();
        if (ClimateMode is not ("full" or "off")) ClimateMode = "";

        if (ScaleOverride != 0) ScaleOverride = (int)Clamp(ScaleOverride, 1f, 16f, 0f);
        if (VerticalExaggerationOverride != 0f)
            VerticalExaggerationOverride = Clamp(VerticalExaggerationOverride, 0.05f, 20f, 0f);
    }

    /// <summary>Clamps, substituting <paramref name="fallback"/> for NaN and other nonsense.</summary>
    private static float Clamp(float value, float min, float max, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
        return value < min ? min : value > max ? max : value;
    }
}
