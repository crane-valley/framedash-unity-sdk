using System;
using UnityEngine.Profiling;

namespace Framedash
{
	/// <summary>
	/// IMemoryMetricsSource backed by UnityEngine.Profiling.Profiler. Unlike the
	/// AsyncReadManagerMetrics I/O source, these two Profiler APIs are always
	/// compiled in (no ENABLE_PROFILER guard) and are safe to call in a release
	/// player -- they simply return 0 there, which this source treats as
	/// "unavailable" per the absent-means-not-collected rule.
	///
	/// GetAllocatedMemoryForGraphicsDriver(): returns the driver-reported
	/// graphics/VRAM allocation, or 0 when the platform doesn't expose it.
	/// GetMonoUsedSizeLong(): managed (Mono/IL2CPP) heap bytes currently in use.
	/// </summary>
	internal sealed class UnityMemoryMetricsSource : IMemoryMetricsSource
	{
		public bool TryReadVram(out long vramBytes)
		{
			vramBytes = 0L;
			try
			{
				long v = Profiler.GetAllocatedMemoryForGraphicsDriver();
				if (v <= 0L) return false;
				vramBytes = v;
				return true;
			}
			catch (Exception)
			{
				// Fail-safe: never let a platform quirk throw into Update().
				return false;
			}
		}

		public bool TryReadHeap(out long heapBytes)
		{
			heapBytes = 0L;
			try
			{
				long v = Profiler.GetMonoUsedSizeLong();
				if (v <= 0L) return false;
				heapBytes = v;
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
