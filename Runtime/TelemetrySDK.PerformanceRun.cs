using System;
using System.Diagnostics;
using System.Threading;

namespace Framedash
{
	public sealed partial class TelemetrySDK
	{
		private PerformanceRunCapture _performanceRun;
		private long _performanceRunTick;
		private int _performanceRunDropBaseline;

		public bool BeginPerformanceRun(PerformanceRunOptions options)
		{
			try
			{
				if (!_initialized || Thread.CurrentThread.ManagedThreadId != _mainThreadId || _performanceRun != null
					|| !PerformanceRunCapture.ValidLabel(_session.ResolveSessionStamp(_buildId, null).BuildId)
					|| !PerformanceRunCapture.TryCreate(options, out var capture, SdkVersion)) return false;
				int dropBaseline = _buffer.DroppedCount;
				if (!TrackInternal("perf_run_start", "", 0, 0, 0, TelemetrySource.Automated,
					capture.StartAttributes(), null, attachPerformance: false, preserveBufferedEvents: true)) return false;
				_performanceRun = capture;
				_performanceRunTick = 0;
				_performanceRunDropBaseline = dropBaseline;
				return true;
			}
			catch (Exception) { return false; }
		}

		public bool EndPerformanceRun(bool completed = true)
		{
			try
			{
				if (!_initialized || Thread.CurrentThread.ManagedThreadId != _mainThreadId || _performanceRun == null) return false;
				var capture = _performanceRun;
				_performanceRun = null;
				// Overflow can evict the start marker before the end is admitted.
				bool retained = _buffer.DroppedCount == _performanceRunDropBaseline;
				bool admitted = TrackInternal("perf_run_end", "", 0, 0, 0, TelemetrySource.Automated,
					capture.EndAttributes(completed && retained), capture.Metrics(),
					attachPerformance: false, preserveBufferedEvents: true);
				return admitted && completed && capture.IsComplete
					&& _buffer.DroppedCount == _performanceRunDropBaseline;
			}
			catch (Exception) { return false; }
		}

		private void UpdatePerformanceRun()
		{
			if (_performanceRun == null || _performanceRun.WindowEnded) return;
			long now = Stopwatch.GetTimestamp();
			// The first callback includes time before the caller began the run.
			if (_performanceRunTick != 0) _performanceRun.Record((now - _performanceRunTick) * 1000d / Stopwatch.Frequency);
			_performanceRunTick = now;
		}
	}
}
