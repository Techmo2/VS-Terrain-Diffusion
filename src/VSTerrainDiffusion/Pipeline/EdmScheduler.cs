/*
 * Copyright 2024 TSAIL Team and The HuggingFace Team. All rights reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 * DISCLAIMER: This file is strongly influenced by
 * https://github.com/LuChengTHU/dpm-solver and https://github.com/NVlabs/edm
 */

using System;

namespace VSTerrainDiffusion.Pipeline;

/// <summary>
/// EDMDPMSolverMultistepScheduler with a Karras sigma schedule and the DPM-Solver++ second-order
/// multistep update. Only the "dpmsolver++" / "midpoint" / "karras" configuration is implemented,
/// which is what the 20-step coarse model uses.
/// </summary>
public sealed class EdmScheduler
{
    public const float SigmaData = 0.5f;
    public const float SigmaMin = 0.002f;
    public const float SigmaMax = 80.0f;
    private const float Rho = 7.0f;

    /// <summary>Sigmas (numSteps + 1 entries; the last is zero).</summary>
    public readonly float[] Sigmas;

    /// <summary>Timesteps, c_noise = 0.25 * log(sigma).</summary>
    public readonly float[] Timesteps;

    private readonly int _numSteps;

    private int _stepIndex;
    private int _lowerOrderNums;
    private float[] _prevModelOutput;

    public EdmScheduler(int numSteps)
    {
        _numSteps = numSteps;
        Sigmas = ComputeKarrasSigmas(numSteps);
        Timesteps = new float[numSteps];
        for (int i = 0; i < numSteps; i++) Timesteps[i] = 0.25f * (float)Math.Log(Sigmas[i]);
        Reset();
    }

    public void Reset()
    {
        _stepIndex = 0;
        _lowerOrderNums = 0;
        _prevModelOutput = null;
    }

    /// <summary>c_in scaling: sample / sqrt(sigma^2 + sigma_data^2).</summary>
    public static float[] PreconditionInputs(float[] sample, float sigma)
    {
        float cIn = 1.0f / (float)Math.Sqrt(sigma * sigma + SigmaData * SigmaData);
        var output = new float[sample.Length];
        for (int i = 0; i < sample.Length; i++) output[i] = sample[i] * cIn;
        return output;
    }

    /// <summary>trigflow_precondition_noise: atan(sigma / sigma_data).</summary>
    public static float TrigflowPreconditionNoise(float sigma) => (float)Math.Atan(sigma / SigmaData);

    /// <summary>Converts a raw model output to x0_pred using the EDM precondition_outputs formula.</summary>
    public static float[] PreconditionOutputs(float[] sample, float[] modelOut, float sigma)
    {
        float sd2 = SigmaData * SigmaData;
        float sig2 = sigma * sigma;
        float cSkip = sd2 / (sig2 + sd2);
        float cOut = sigma * SigmaData / (float)Math.Sqrt(sig2 + sd2);
        var x0 = new float[sample.Length];
        for (int i = 0; i < sample.Length; i++) x0[i] = cSkip * sample[i] + cOut * modelOut[i];
        return x0;
    }

    /// <summary>Runs one DPM-Solver++ step and returns the sample at the next (lower) sigma.</summary>
    public float[] Step(float[] modelOut, float[] sample)
    {
        float sigmaS = Sigmas[_stepIndex];
        float sigmaT = Sigmas[_stepIndex + 1];

        float[] x0Pred = PreconditionOutputs(sample, modelOut, sigmaS);

        // final_sigmas_type == "zero", so the last step always drops to first order.
        bool lowerOrderFinal = _stepIndex == _numSteps - 1;

        float[] prevSample;
        if (_lowerOrderNums < 1 || lowerOrderFinal)
        {
            prevSample = FirstOrderUpdate(x0Pred, sample, sigmaS, sigmaT);
        }
        else
        {
            prevSample = SecondOrderUpdate(_prevModelOutput, x0Pred, sample, sigmaS, sigmaT, Sigmas[_stepIndex - 1]);
        }

        _prevModelOutput = x0Pred;
        if (_lowerOrderNums < 2) _lowerOrderNums++;
        _stepIndex++;
        return prevSample;
    }

    /// <summary>
    /// DPM-Solver++ first-order update with alpha = 1 (no VP conversion):
    /// x_t = (sigma_t/sigma_s) * sample - (sigma_t/sigma_s - 1) * D0.
    /// </summary>
    private static float[] FirstOrderUpdate(float[] x0Pred, float[] sample, float sigmaS, float sigmaT)
    {
        float ratio = sigmaT / sigmaS;
        var xt = new float[sample.Length];
        for (int i = 0; i < sample.Length; i++) xt[i] = ratio * sample[i] - (ratio - 1.0f) * x0Pred[i];
        return xt;
    }

    /// <summary>DPM-Solver++ second-order midpoint update.</summary>
    private static float[] SecondOrderUpdate(float[] m1, float[] m0, float[] sample,
                                             float sigmaS0, float sigmaT, float sigmaS1)
    {
        double lT = -Math.Log(sigmaT);
        double lS0 = -Math.Log(sigmaS0);
        double lS1 = -Math.Log(sigmaS1);
        double h = lT - lS0;
        double h0 = lS0 - lS1;
        float r0 = (float)(h0 / h);

        float expNH = sigmaT / sigmaS0; // exp(-h), computed in float32 to match Python
        float sCoeff = sigmaT / sigmaS0;
        float d0Coeff = -(expNH - 1.0f);
        float d1Coeff = -0.5f * (expNH - 1.0f);

        var xt = new float[sample.Length];
        for (int i = 0; i < sample.Length; i++)
        {
            float d1 = (m0[i] - m1[i]) / r0;
            xt[i] = sCoeff * sample[i] + d0Coeff * m0[i] + d1Coeff * d1;
        }
        return xt;
    }

    /// <summary>Karras schedule: sigmas[i] = (maxInv + i/(n-1) * (minInv - maxInv))^rho, with a trailing zero.</summary>
    public static float[] ComputeKarrasSigmas(int n)
    {
        float minInvRho = (float)Math.Pow(SigmaMin, 1.0 / Rho);
        float maxInvRho = (float)Math.Pow(SigmaMax, 1.0 / Rho);
        var sigmas = new float[n + 1];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / (n - 1);
            float invRho = maxInvRho + t * (minInvRho - maxInvRho);
            sigmas[i] = (float)Math.Pow(invRho, Rho);
        }
        sigmas[n] = 0.0f;
        return sigmas;
    }
}
