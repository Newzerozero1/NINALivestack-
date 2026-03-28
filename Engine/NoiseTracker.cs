using System;
using System.Collections.Generic;

namespace NinaLiveStack.Engine {

    /// <summary>
    /// Tracks noise level after each frame is added to the stack.
    /// Stores the history so the UI can display a noise reduction graph
    /// showing how SNR improves with each frame.
    /// </summary>
    public class NoiseTracker {

        private readonly List<NoiseDataPoint> _history = new List<NoiseDataPoint>();
        private float _firstFrameNoise = 0f;

        public IReadOnlyList<NoiseDataPoint> History => _history;
        public float FirstFrameNoise => _firstFrameNoise;
        public float CurrentNoise => _history.Count > 0 ? _history[_history.Count - 1].MeasuredNoise : 0f;
        public float CurrentSNRGain => _history.Count > 0 ? _history[_history.Count - 1].MeasuredSNRGain : 1f;

        /// <summary>
        /// Record noise measurement after a new frame is stacked.
        /// Call this with the current stack's green channel (or luminance).
        /// </summary>
        public void RecordFrame(float[] stackChannel, int width, int height, int frameCount) {
            float noise = AdvancedStretch.EstimateNoise(stackChannel, width, height);

            if (frameCount == 1 || _firstFrameNoise == 0f) {
                _firstFrameNoise = noise;
            }

            float theoreticalSNR = (float)Math.Sqrt(frameCount);
            float measuredSNR = (_firstFrameNoise > 0.00001f) ? _firstFrameNoise / noise : theoreticalSNR;

            _history.Add(new NoiseDataPoint {
                FrameNumber = frameCount,
                MeasuredNoise = noise,
                TheoreticalSNRGain = theoreticalSNR,
                MeasuredSNRGain = measuredSNR
            });
        }

        public void Reset() {
            _history.Clear();
            _firstFrameNoise = 0f;
        }

        /// <summary>
        /// Get a formatted status string for the UI.
        /// </summary>
        public string GetStatusText() {
            if (_history.Count == 0) return "No frames";

            var latest = _history[_history.Count - 1];
            return $"Frame {latest.FrameNumber}: " +
                   $"SNR ×{latest.MeasuredSNRGain:F1} " +
                   $"(theoretical ×{latest.TheoreticalSNRGain:F1}) | " +
                   $"Noise: {latest.MeasuredNoise:E2}";
        }
    }

    public class NoiseDataPoint {
        public int FrameNumber { get; set; }
        public float MeasuredNoise { get; set; }
        public float TheoreticalSNRGain { get; set; }
        public float MeasuredSNRGain { get; set; }
    }
}
