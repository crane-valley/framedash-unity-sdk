using System;
using UnityEngine.Profiling;

namespace Framedash
{
	// These Profiler APIs remain callable in release builds, so no ENABLE_PROFILER guard is needed.
	// Zero means the platform did not collect the metric, rather than measured zero usage.
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
				// Platform profiler failures must not escape into the game's Update loop.
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
