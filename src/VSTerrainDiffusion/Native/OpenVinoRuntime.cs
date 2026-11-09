using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

#nullable enable

namespace VSTerrainDiffusion.Native;

/// <summary>
/// A small managed wrapper around OpenVINO's stable C API. This is intentionally independent of
/// ONNX Runtime: it lets one model use OpenVINO without loading a second ORT core or execution-
/// provider bridge into the server process.
/// </summary>
public sealed class OpenVinoRuntime : IDisposable
{
    /// <summary>The C API's import name, resolved by <see cref="OpenVinoLibraryResolver"/>.</summary>
    private const string NativeLibraryName = "openvino_c";

    /// <summary>One inference at a time on the single request; also guards disposal.</summary>
    private readonly object _runGate = new();

    /// <summary>How many inputs the compiled model takes.</summary>
    private readonly int _inputCount;

    /// <summary>The <c>ov_core_t</c> handle.</summary>
    private IntPtr _core;

    /// <summary>The <c>ov_compiled_model_t</c> handle.</summary>
    private IntPtr _compiledModel;

    /// <summary>The <c>ov_infer_request_t</c> handle every run reuses.</summary>
    private IntPtr _inferRequest;

    /// <summary>Whether the handles have been freed.</summary>
    private bool _disposed;

    /// <summary>Configure where the standalone OpenVINO libraries were installed.</summary>
    internal static void ConfigureNativeDirectory(string directory)
    {
        OpenVinoLibraryResolver.Configure(directory);
    }

    /// <summary>
    /// Compile an ONNX model for Intel CPU inference. OpenVINO's compiled-model cache is stored in
    /// <paramref name="cacheDirectory"/> and reused on subsequent starts.
    /// </summary>
    public OpenVinoRuntime(string modelPath, string cacheDirectory, int threadCount,
                           Action<string>? progress = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("OpenVINO model not found", modelPath);

        string fullModelPath = Path.GetFullPath(modelPath);
        string fullCachePath = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(fullCachePath);
        threadCount = Math.Max(1, threadCount);

        try
        {
            progress?.Invoke("creating OpenVINO core");
            Check(Native.ov_core_create(out _core), "create core");
            progress?.Invoke("core created; configuring hosted CPU execution");
            SetCpuProperty("CACHE_DIR", fullCachePath);
            SetCpuProperty("NUM_STREAMS", "1");
            SetCpuProperty("INFERENCE_NUM_THREADS", threadCount.ToString(CultureInfo.InvariantCulture));
            SetCpuProperty("PERFORMANCE_HINT", "LATENCY");
            progress?.Invoke("CPU execution configured; reading model");
            IntPtr model = IntPtr.Zero;
            try
            {
                Check(Native.ov_core_read_model(_core, fullModelPath, IntPtr.Zero, out model),
                    "read model");
                progress?.Invoke("model read; compiling for CPU");
                Check(Native.ov_core_compile_model_default(
                        _core, model, "CPU", 0, out _compiledModel),
                    "compile model");
            }
            finally
            {
                if (model != IntPtr.Zero) Native.ov_model_free(model);
            }
            progress?.Invoke("model compiled; creating inference request");
            Check(Native.ov_compiled_model_create_infer_request(_compiledModel, out _inferRequest),
                "create inference request");
            progress?.Invoke("inference request created; validating model metadata");

            Check(Native.ov_compiled_model_inputs_size(_compiledModel, out nuint inputs),
                "read model input count");
            Check(Native.ov_compiled_model_outputs_size(_compiledModel, out nuint outputs),
                "read model output count");
            if (inputs > int.MaxValue || outputs != 1)
            {
                throw new NotSupportedException(
                    $"OpenVINO model has {inputs} inputs and {outputs} outputs; one output is required");
            }
            _inputCount = (int)inputs;
            progress?.Invoke("ready");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Run the model with float32 tensors in declared input order.</summary>
    public float[] Run(IReadOnlyList<(float[] Data, long[] Shape)> inputs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenVinoRuntime));
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        if (inputs.Count != _inputCount)
            throw new ArgumentException($"OpenVINO model expects {_inputCount} inputs but got {inputs.Count}");

        lock (_runGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OpenVinoRuntime));

            var pins = new GCHandle[inputs.Count];
            var tensors = new IntPtr[inputs.Count];
            IntPtr output = IntPtr.Zero;
            try
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    (float[] data, long[] dimensions) = inputs[i];
                    ValidateTensor(data, dimensions, i);

                    pins[i] = GCHandle.Alloc(data, GCHandleType.Pinned);
                    GCHandle shapePin = GCHandle.Alloc(dimensions, GCHandleType.Pinned);
                    try
                    {
                        var shape = new OvShape
                        {
                            Rank = dimensions.LongLength,
                            Dimensions = shapePin.AddrOfPinnedObject()
                        };
                        Check(Native.ov_tensor_create_from_host_ptr(
                                OvElementType.F32, shape, pins[i].AddrOfPinnedObject(), out tensors[i]),
                            $"create input tensor {i}");
                    }
                    finally
                    {
                        shapePin.Free();
                    }

                    Check(Native.ov_infer_request_set_input_tensor_by_index(
                            _inferRequest, (nuint)i, tensors[i]),
                        $"bind input tensor {i}");
                }

                Check(Native.ov_infer_request_infer(_inferRequest), "run inference");
                Check(Native.ov_infer_request_get_output_tensor_by_index(_inferRequest, 0, out output),
                    "get output tensor");
                Check(Native.ov_tensor_get_element_type(output, out OvElementType outputType),
                    "read output type");
                if (outputType != OvElementType.F32)
                    throw new NotSupportedException($"OpenVINO model returned {outputType}; float32 is required");

                Check(Native.ov_tensor_get_size(output, out nuint elementCount), "read output size");
                if (elementCount > int.MaxValue)
                    throw new NotSupportedException($"OpenVINO output has {elementCount} elements");
                Check(Native.ov_tensor_data(output, out IntPtr outputData), "read output data");

                var result = new float[(int)elementCount];
                Marshal.Copy(outputData, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (output != IntPtr.Zero) Native.ov_tensor_free(output);
                for (int i = tensors.Length - 1; i >= 0; i--)
                {
                    if (tensors[i] != IntPtr.Zero) Native.ov_tensor_free(tensors[i]);
                    if (pins[i].IsAllocated) pins[i].Free();
                }
            }
        }
    }

    /// <summary>Checks that an input's shape is positive and matches its buffer.</summary>
    /// <param name="data">The input's values.</param>
    /// <param name="dimensions">The input's shape.</param>
    /// <param name="index">The input's position, for the message.</param>
    /// <exception cref="ArgumentException">The shape is empty, not positive or the wrong size.</exception>
    private static void ValidateTensor(float[] data, long[] dimensions, int index)
    {
        if (data == null) throw new ArgumentNullException($"inputs[{index}].Data");
        if (dimensions == null || dimensions.Length == 0)
            throw new ArgumentException($"Input {index} has no dimensions");

        long elements = 1;
        foreach (long dimension in dimensions)
        {
            if (dimension <= 0) throw new ArgumentException($"Input {index} has an invalid shape");
            elements = checked(elements * dimension);
        }
        if (elements != data.LongLength)
            throw new ArgumentException(
                $"Input {index} shape contains {elements} elements but its buffer contains {data.LongLength}");
    }

    /// <summary>Sets one property of the CPU device.</summary>
    /// <param name="key">The property name.</param>
    /// <param name="value">Its value, as text.</param>
    private void SetCpuProperty(string key, string value)
    {
        Check(Native.ov_core_set_property_one(_core, "CPU", key, value),
            $"set CPU property {key}");
    }

    /// <summary>Throws if a C API call failed, with OpenVINO's own description.</summary>
    /// <param name="status">The call's status.</param>
    /// <param name="operation">What was being done, for the message.</param>
    /// <exception cref="InvalidOperationException">The status is not OK.</exception>
    private static void Check(OvStatus status, string operation)
    {
        if (status == OvStatus.Ok) return;
        string? message = Marshal.PtrToStringUTF8(Native.ov_get_error_info(status));
        throw new InvalidOperationException($"OpenVINO could not {operation} ({status}): {message}");
    }

    /// <summary>Frees the request, the compiled model and the core. Waits for a run in progress.</summary>
    public void Dispose()
    {
        lock (_runGate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_inferRequest != IntPtr.Zero) Native.ov_infer_request_free(_inferRequest);
            if (_compiledModel != IntPtr.Zero) Native.ov_compiled_model_free(_compiledModel);
            if (_core != IntPtr.Zero) Native.ov_core_free(_core);
            _inferRequest = IntPtr.Zero;
            _compiledModel = IntPtr.Zero;
            _core = IntPtr.Zero;
        }
    }

    /// <summary><c>ov_shape_t</c>: a rank and a pointer to that many 64-bit dimensions.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct OvShape
    {
        /// <summary>Number of dimensions.</summary>
        public long Rank;

        /// <summary>Pointer to the dimensions, pinned for the call.</summary>
        public IntPtr Dimensions;
    }

    /// <summary><c>ov_status_e</c>; only success is named, every other value is an error.</summary>
    private enum OvStatus
    {
        /// <summary>The call succeeded.</summary>
        Ok = 0
    }

    /// <summary><c>ov_element_type_e</c>; only the one type used here is named.</summary>
    private enum OvElementType : uint
    {
        /// <summary>32-bit IEEE float.</summary>
        F32 = 5
    }

    /// <summary>
    /// The OpenVINO 2022.3 C API entry points used here. See <c>openvino/c/*.h</c> for each.
    /// </summary>
    private static class Native
    {
        /// <summary>Creates a core.</summary>
        /// <param name="core">Receives the core handle.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_core_create(out IntPtr core);

        /// <summary>Sets one property on a device.</summary>
        /// <remarks>
        /// The 2022.3 C API has no non-variadic property setter. Its implementation consumes one
        /// key/value pair per call, so this declaration supplies exactly that pair.
        /// </remarks>
        /// <param name="core">The core.</param>
        /// <param name="deviceName">The device, e.g. "CPU".</param>
        /// <param name="propertyKey">The property name.</param>
        /// <param name="propertyValue">Its value.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ov_core_set_property")]
        internal static extern OvStatus ov_core_set_property_one(
            IntPtr core,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string propertyKey,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string propertyValue);

        /// <summary>Reads an ONNX or IR model.</summary>
        /// <param name="core">The core.</param>
        /// <param name="modelPath">The model file.</param>
        /// <param name="binPath">The IR weights file, or null for ONNX.</param>
        /// <param name="model">Receives the model handle.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ov_core_read_model")]
        internal static extern OvStatus ov_core_read_model(
            IntPtr core,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
            IntPtr binPath,
            out IntPtr model);

        /// <summary>Compiles a model for a device with no extra properties.</summary>
        /// <param name="core">The core.</param>
        /// <param name="model">The model.</param>
        /// <param name="deviceName">The device.</param>
        /// <param name="propertyArgsSize">Number of variadic property arguments; always 0 here.</param>
        /// <param name="compiledModel">Receives the compiled model handle.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ov_core_compile_model")]
        internal static extern OvStatus ov_core_compile_model_default(
            IntPtr core,
            IntPtr model,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string deviceName,
            nuint propertyArgsSize,
            out IntPtr compiledModel);

        /// <summary>Frees a model handle.</summary>
        /// <param name="model">The model.</param>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ov_model_free(IntPtr model);

        /// <summary>Number of inputs of a compiled model.</summary>
        /// <param name="compiledModel">The compiled model.</param>
        /// <param name="size">Receives the count.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_compiled_model_inputs_size(IntPtr compiledModel, out nuint size);

        /// <summary>Number of outputs of a compiled model.</summary>
        /// <param name="compiledModel">The compiled model.</param>
        /// <param name="size">Receives the count.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_compiled_model_outputs_size(IntPtr compiledModel, out nuint size);

        /// <summary>Creates an inference request.</summary>
        /// <param name="compiledModel">The compiled model.</param>
        /// <param name="inferRequest">Receives the request handle.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_compiled_model_create_infer_request(
            IntPtr compiledModel, out IntPtr inferRequest);

        /// <summary>Wraps caller memory as a tensor, without copying.</summary>
        /// <param name="type">Element type.</param>
        /// <param name="shape">The shape.</param>
        /// <param name="hostPointer">The data, pinned for the tensor's life.</param>
        /// <param name="tensor">Receives the tensor handle.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_tensor_create_from_host_ptr(
            OvElementType type, OvShape shape, IntPtr hostPointer, out IntPtr tensor);

        /// <summary>Binds an input tensor.</summary>
        /// <param name="inferRequest">The request.</param>
        /// <param name="index">The input's position.</param>
        /// <param name="tensor">The tensor.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_infer_request_set_input_tensor_by_index(
            IntPtr inferRequest, nuint index, IntPtr tensor);

        /// <summary>Runs inference synchronously.</summary>
        /// <param name="inferRequest">The request.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_infer_request_infer(IntPtr inferRequest);

        /// <summary>Gets an output tensor, which the caller frees.</summary>
        /// <param name="inferRequest">The request.</param>
        /// <param name="index">The output's position.</param>
        /// <param name="tensor">Receives the tensor handle.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_infer_request_get_output_tensor_by_index(
            IntPtr inferRequest, nuint index, out IntPtr tensor);

        /// <summary>A tensor's element type.</summary>
        /// <param name="tensor">The tensor.</param>
        /// <param name="type">Receives the type.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_tensor_get_element_type(IntPtr tensor, out OvElementType type);

        /// <summary>A tensor's element count.</summary>
        /// <param name="tensor">The tensor.</param>
        /// <param name="elementCount">Receives the count.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_tensor_get_size(IntPtr tensor, out nuint elementCount);

        /// <summary>A pointer to a tensor's data, owned by the tensor.</summary>
        /// <param name="tensor">The tensor.</param>
        /// <param name="data">Receives the pointer.</param>
        /// <returns>The status.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OvStatus ov_tensor_data(IntPtr tensor, out IntPtr data);

        /// <summary>A static description of a status.</summary>
        /// <param name="status">The status.</param>
        /// <returns>A UTF-8 string owned by OpenVINO.</returns>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ov_get_error_info(OvStatus status);

        /// <summary>Frees a tensor handle.</summary>
        /// <param name="tensor">The tensor.</param>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ov_tensor_free(IntPtr tensor);

        /// <summary>Frees a request handle.</summary>
        /// <param name="inferRequest">The request.</param>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ov_infer_request_free(IntPtr inferRequest);

        /// <summary>Frees a compiled model handle.</summary>
        /// <param name="compiledModel">The compiled model.</param>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ov_compiled_model_free(IntPtr compiledModel);

        /// <summary>Frees a core handle.</summary>
        /// <param name="core">The core.</param>
        [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ov_core_free(IntPtr core);
    }
}
