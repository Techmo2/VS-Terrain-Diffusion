namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// Supplies "what climate does this row of the conditioning map sit in" to the coarse model.
///
/// The counterpart of <see cref="ILandmaskSource"/> for the two climate channels. Left to itself
/// the pipeline draws temperature and rainfall as noise matched to the real-world distribution,
/// which is a climatology with continents, oceans and rain shadows in it but no north-south axis:
/// the cold places are wherever the noise put them. Handing it a band instead makes the world's
/// <c>polarEquatorDistance</c> decide where the tropics and the ice are, leaving the model to
/// decide what a coast, a rain shadow or a continental interior does to the climate <em>within</em>
/// that band.
///
/// Coordinates are coarse pixel rows in the model's own frame - the row index
/// <see cref="SyntheticMapFactory.Sample"/> takes as its y, one pixel per 256 native pixels. Rows
/// run along Z, which is the axis Vintage Story measures latitude on.
/// </summary>
public interface ILatitudeSource
{
    /// <summary>True when there is no banding to apply and the channels should be left alone.</summary>
    bool IsNeutral { get; }

    /// <summary>
    /// The mean annual temperature and annual rainfall this row's band asks the model for, in
    /// degrees Celsius and millimetres. Absolute values, not shifts: a band is a statement about
    /// what the climate at that latitude <em>is</em>, and the noise the map already carries becomes
    /// the region's departure from it rather than the climate itself.
    /// </summary>
    void BandAt(int coarseRow, out float temperatureC, out float precipitationMm);
}
