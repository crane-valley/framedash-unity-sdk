using UnityEngine;
using UnityEngine.Profiling;

namespace Framedash
{
	/// <summary>
	/// Collects performance metrics each frame.
	/// Call <see cref="UpdateFrameTimings"/> every frame from Update() so
	/// FrameTimingManager data stays fresh. <see cref="Collect"/> reads the
	/// cached values without re-capturing.
	/// </summary>
	public sealed class PerformanceCollector
	{
		public struct PerfSnapshot
		{
			public float Fps;
			public float FrameTimeMs;
			public long MemoryUsedBytes;
			public float GpuTimeMs;
			public float GameThreadMs;
			public float RenderThreadMs;
		}

		// Reusable buffer — avoids per-call GC allocation.
		private readonly FrameTiming[] _timings = new FrameTiming[1];

		// Cached per-frame values from FrameTimingManager.
		private float _cachedGpuTimeMs;
		private float _cachedGameThreadMs;
		private float _cachedRenderThreadMs;

		/// <summary>
		/// Capture FrameTimingManager data and cache GPU / CPU thread times.
		/// Must be called once per frame (e.g., from MonoBehaviour.Update)
		/// so that <see cref="Collect"/> always reads a recent sample.
		/// ~4 frame delay for GPU data is inherent (async GPU measurement).
		/// Returns 0 when unavailable (Player Settings disabled, unsupported platform).
		/// </summary>
		public void UpdateFrameTimings()
		{
			FrameTimingManager.CaptureFrameTimings();
			uint count = FrameTimingManager.GetLatestTimings(1, _timings);
			if (count == 0)
			{
				// Signal "unavailable" per proto contract (0 = not collected).
				_cachedGpuTimeMs = 0f;
				_cachedGameThreadMs = 0f;
				_cachedRenderThreadMs = 0f;
				return;
			}

			_cachedGpuTimeMs = (float)_timings[0].gpuFrameTime;
			_cachedGameThreadMs = (float)_timings[0].cpuMainThreadFrameTime;
			_cachedRenderThreadMs = (float)_timings[0].cpuRenderThreadFrameTime;
		}

		/// <summary>Collect current frame performance data using cached timings.</summary>
		public PerfSnapshot Collect()
		{
			float deltaTime = Time.unscaledDeltaTime;
			float rawFrameTimeMs = deltaTime * 1000f;
			// Clamp the real-time frame delta to the ingest frame-time ceiling (10000ms):
			// a long pause/resume gap would otherwise emit a frame_time the validator
			// rejects (dropping the whole batch).
			float frameTimeMs = FieldClamp.ClampTimingMs(rawFrameTimeMs);

			return new PerfSnapshot
			{
				// Derive FPS from the RAW frame delta (only the high end is capped, to
				// 1000): deriving it from the CLAMPED frame time would report a long
				// (>10s) frame as 0.1 fps instead of its true lower rate. fps down to 0
				// is valid to ingest.
				Fps = FieldClamp.FpsFromFrameTimeMs(rawFrameTimeMs),
				FrameTimeMs = frameTimeMs,
				MemoryUsedBytes = FieldClamp.ClampMemory(Profiler.GetTotalAllocatedMemoryLong()),
				// FrameTimingManager values can be NaN/huge; clamp each to [0, 10000].
				GpuTimeMs = FieldClamp.ClampTimingMs(_cachedGpuTimeMs),
				GameThreadMs = FieldClamp.ClampTimingMs(_cachedGameThreadMs),
				RenderThreadMs = FieldClamp.ClampTimingMs(_cachedRenderThreadMs),
			};
		}
	}
}
