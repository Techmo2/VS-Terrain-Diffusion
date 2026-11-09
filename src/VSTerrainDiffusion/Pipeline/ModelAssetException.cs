using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>Raised when the model files cannot be made available locally.</summary>
public class ModelAssetException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is missing and how to get it, for the player.</param>
    public ModelAssetException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is missing and how to get it, for the player.</param>
    /// <param name="inner">The underlying failure.</param>
    public ModelAssetException(string message, Exception inner) : base(message, inner) { }
}
