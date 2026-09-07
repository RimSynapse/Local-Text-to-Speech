using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RimSynapse;

namespace RimSynapse.LocalTts
{
    /// <summary>
    /// Wraps the Kokoro ONNX <see cref="InferenceSession"/>. Selects the DirectML execution
    /// provider when GPU acceleration is requested and available, otherwise runs on CPU. Input
    /// binding is resolved from the model's own metadata (by element type + shape) so the code is
    /// not sensitive to the exact input names in a given model export.
    /// </summary>
    public sealed class KokoroSession : IDisposable
    {
        private InferenceSession _session;
        private string _inputIdsName, _styleName, _speedName, _outputName;

        /// <summary>Which backend the session actually initialized on.</summary>
        public string ActiveProvider { get; private set; } = "None";

        /// <summary>True when the model is resident on the GPU (DirectML), false on CPU.</summary>
        public bool OnGpu { get; private set; }

        public bool IsReady => _session != null;

        /// <summary>Load the model, trying GPU (DirectML) first when requested.</summary>
        public bool Load(string modelPath, bool preferGpu)
        {
            if (_session != null) return true;

            if (preferGpu && TryCreate(modelPath, useDml: true))
            {
                ActiveProvider = "DirectML (GPU)";
                OnGpu = true;
                SynapseLogger.Message("[LocalTTS] Kokoro session on DirectML (GPU).");
            }
            else if (TryCreate(modelPath, useDml: false))
            {
                ActiveProvider = "CPU";
                OnGpu = false;
                SynapseLogger.Message("[LocalTTS] Kokoro session on CPU.");
            }
            else
            {
                SynapseLogger.Error("[LocalTTS] Failed to create an ONNX session on any backend.");
                return false;
            }

            ResolveIoNames();
            return true;
        }

        private bool TryCreate(string modelPath, bool useDml)
        {
            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };

                if (useDml)
                {
                    // DirectML requires sequential execution and no arena memory pattern.
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML(0);
                }

                _session = new InferenceSession(modelPath, options);
                return true;
            }
            catch (Exception ex)
            {
                if (useDml)
                    SynapseLogger.Warning($"[LocalTTS] DirectML provider unavailable ({ex.Message}); falling back to CPU.");
                else
                    SynapseLogger.Error($"[LocalTTS] CPU session creation failed: {ex.Message}");
                _session = null;
                return false;
            }
        }

        private void ResolveIoNames()
        {
            foreach (var kv in _session.InputMetadata)
            {
                var meta = kv.Value;
                if (meta.ElementType == typeof(long) || meta.ElementType == typeof(int))
                {
                    _inputIdsName = kv.Key;
                }
                else if (meta.ElementType == typeof(float))
                {
                    // style is the 256-wide 2-D input; speed is the scalar/1-element input.
                    int lastDim = meta.Dimensions != null && meta.Dimensions.Length > 0
                        ? meta.Dimensions[meta.Dimensions.Length - 1] : -1;
                    if (lastDim == VoiceStyleBank.Dim || (meta.Dimensions != null && meta.Dimensions.Length >= 2))
                        _styleName = kv.Key;
                    else
                        _speedName = kv.Key;
                }
            }

            // Fall back to canonical names if metadata was ambiguous.
            _inputIdsName ??= "input_ids";
            _styleName ??= "style";
            _speedName ??= "speed";
            _outputName = _session.OutputMetadata.Keys.FirstOrDefault() ?? "waveform";

            SynapseLogger.Message($"[LocalTTS] Model IO -> ids='{_inputIdsName}', style='{_styleName}', " +
                                  $"speed='{_speedName}', out='{_outputName}'.");
        }

        /// <summary>
        /// Run inference. <paramref name="innerIds"/> is the phoneme token sequence WITHOUT pads;
        /// this method wraps it with the leading/trailing 0 pad the model expects.
        /// </summary>
        public float[] Run(IReadOnlyList<long> innerIds, float[] style, float speed)
        {
            if (_session == null || innerIds == null || innerIds.Count == 0 || style == null)
                return Array.Empty<float>();

            // [0, ...ids..., 0]
            var padded = new long[innerIds.Count + 2];
            for (int i = 0; i < innerIds.Count; i++) padded[i + 1] = innerIds[i];

            var idsTensor = new DenseTensor<long>(padded, new[] { 1, padded.Length });
            var styleTensor = new DenseTensor<float>(style, new[] { 1, VoiceStyleBank.Dim });
            var speedTensor = new DenseTensor<float>(new[] { speed }, new[] { 1 });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputIdsName, idsTensor),
                NamedOnnxValue.CreateFromTensor(_styleName, styleTensor),
                NamedOnnxValue.CreateFromTensor(_speedName, speedTensor),
            };

            using (var results = _session.Run(inputs))
            {
                var first = results.FirstOrDefault();
                if (first == null) return Array.Empty<float>();
                return first.AsTensor<float>().ToArray();
            }
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
