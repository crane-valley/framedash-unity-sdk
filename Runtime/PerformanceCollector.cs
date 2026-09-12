using UnityEngine;
using UnityEngine.Profiling;

namespace Framedash
{
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

		private readonly FrameTiming[] _timings = new FrameTiming[1];
		private readonly object _snapshotGate = new object();
		private PerfSnapshot _snapshot;

		public void UpdateFrameTimings()
		{
			float rawFrameTimeMs = Time.unscaledDeltaTime * 1000f;
			long memoryUsedBytes = FieldClamp.ClampMemory(Profiler.GetTotalAllocatedMemoryLong());
			FrameTimingManager.CaptureFrameTimings();
			uint count = FrameTimingManager.GetLatestTimings(1, _timings);
			var snapshot = new PerfSnapshot
			{
				// FPS must reflect pauses longer than the ingest frame-time ceiling.
				Fps = FieldClamp.FpsFromFrameTimeMs(rawFrameTimeMs),
				FrameTimeMs = FieldClamp.ClampTimingMs(rawFrameTimeMs),
				MemoryUsedBytes = memoryUsedBytes,
				GpuTimeMs = count == 0 ? 0f : FieldClamp.ClampTimingMs((float)_timings[0].gpuFrameTime),
				GameThreadMs = count == 0 ? 0f : FieldClamp.ClampTimingMs((float)_timings[0].cpuMainThreadFrameTime),
				RenderThreadMs = count == 0 ? 0f : FieldClamp.ClampTimingMs((float)_timings[0].cpuRenderThreadFrameTime),
			};
			lock (_snapshotGate) _snapshot = snapshot;
		}

		public PerfSnapshot Collect()
		{
			// Worker reads must not access Unity APIs or mix two refreshes.
			lock (_snapshotGate) return _snapshot;
		}
	}
}
