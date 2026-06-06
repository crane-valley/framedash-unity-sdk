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

			return new PerfSnapshot
			{
				Fps = deltaTime > 0f ? 1f / deltaTime : 0f,
				FrameTimeMs = deltaTime * 1000f,
				MemoryUsedBytes = Profiler.GetTotalAllocatedMemoryLong(),
				GpuTimeMs = _cachedGpuTimeMs,
				GameThreadMs = _cachedGameThreadMs,
				RenderThreadMs = _cachedRenderThreadMs,
			};
		}
	}
}
