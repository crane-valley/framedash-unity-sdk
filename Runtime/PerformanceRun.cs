#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Framedash
{
	public sealed class PerformanceRunOptions
	{
		public string RunId = "";
		public string Scenario = "";
		public string Hardware = "";
		public string Graphics = "";
		public string Resolution = "";
		public string Configuration = "";
		public string Commit = "";
		public string Branch = "";
		public int WarmupFrames = 120;
		public int TargetFrames = 3600;
	}

	internal sealed class PerformanceRunCapture
	{
		private readonly int[] _histogram = new int[256];
		private readonly int[] _hitches = new int[4];
		private readonly List<StringPair> _attributes;
		private readonly int _warmup;
		private readonly int _target;
		public int Samples { get; private set; }
		public int DroppedSamples { get; private set; }
		public int WarmupSamples { get; private set; }
		public double DurationMs { get; private set; }
		public bool IsComplete => WarmupSamples == _warmup && Samples == _target && DroppedSamples == 0;
		public bool WindowEnded => Samples + DroppedSamples >= _target;

		private PerformanceRunCapture(PerformanceRunOptions options, string sdkVersion)
		{
			_warmup = options.WarmupFrames;
			_target = options.TargetFrames;
			_attributes = new List<StringPair>
			{
				new StringPair("pr.v", "1"), new StringPair("pr.id", options.RunId),
				new StringPair("pr.method", "unity-update-stopwatch-v1"), new StringPair("pr.sdk", sdkVersion),
				new StringPair("pr.scenario", options.Scenario), new StringPair("pr.hardware", options.Hardware),
				new StringPair("pr.graphics", options.Graphics), new StringPair("pr.resolution", options.Resolution),
				new StringPair("pr.configuration", options.Configuration), new StringPair("pr.commit", options.Commit),
				new StringPair("pr.branch", options.Branch),
				new StringPair("pr.warmup", _warmup.ToString(CultureInfo.InvariantCulture)),
				new StringPair("pr.target", _target.ToString(CultureInfo.InvariantCulture)),
			};
		}

		internal static bool ValidLabel(string? value)
		{
			if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
			foreach (char c in value) if (c < 32 || c == 127) return false;
			return true;
		}

		public static bool TryCreate(PerformanceRunOptions? options, out PerformanceRunCapture capture, string sdkVersion = "development")
		{
			capture = null!;
			if (options == null || options.RunId == null || options.RunId.Length != 36 || !Guid.TryParseExact(options.RunId, "D", out _)
				|| options.RunId != options.RunId.ToLowerInvariant() || options.RunId[14] != '4'
				|| "89ab".IndexOf(options.RunId[19]) < 0 || options.WarmupFrames < 0 || options.WarmupFrames > 60000
				|| options.TargetFrames < 1000 || options.TargetFrames > 1000000
				|| !ValidLabel(options.Scenario) || !ValidLabel(options.Hardware) || !ValidLabel(options.Graphics)
				|| !ValidLabel(options.Resolution) || !ValidLabel(options.Configuration)
				|| !ValidLabel(options.Commit) || !ValidLabel(options.Branch) || !ValidLabel(sdkVersion)) return false;
			capture = new PerformanceRunCapture(options, sdkVersion);
			return true;
		}

		public void Record(double milliseconds)
		{
			if (WarmupSamples < _warmup) { WarmupSamples++; return; }
			if (WindowEnded) return;
			if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds <= 0 || milliseconds >= 32768)
			{
				DroppedSamples++;
				return;
			}
			int group = 0;
			double upper = 1;
			while (milliseconds >= upper) { upper *= 2; group++; }
			double lower = group == 0 ? 0 : upper / 2;
			double width = group == 0 ? 1d / 16 : lower / 16;
			int index = group * 16 + Math.Min(15, (int)((milliseconds - lower) / width));
			_histogram[index]++;
			Samples++;
			DurationMs += milliseconds;
			if (milliseconds > 1000d / 60) _hitches[0]++;
			if (milliseconds > 1000d / 30) _hitches[1]++;
			if (milliseconds > 50) _hitches[2]++;
			if (milliseconds > 100) _hitches[3]++;
		}

		public List<StringPair> StartAttributes() => new List<StringPair>(_attributes);

		public List<StringPair> EndAttributes(bool completed = true)
		{
			var attributes = StartAttributes();
			attributes.Add(new StringPair("pr.state", completed && IsComplete ? "complete" : "incomplete"));
			for (int chunk = 0; chunk < 8; chunk++)
			{
				var builder = new StringBuilder(256);
				for (int i = 0; i < 32; i++)
				{
					if (i > 0) builder.Append(',');
					builder.Append(_histogram[chunk * 32 + i].ToString(CultureInfo.InvariantCulture));
				}
				attributes.Add(new StringPair("pr.hist." + chunk.ToString(CultureInfo.InvariantCulture), builder.ToString()));
			}
			return attributes;
		}

		public List<FloatPair> Metrics() => new List<FloatPair>
		{
			new FloatPair("pr.samples", Samples), new FloatPair("pr.dropped", DroppedSamples),
			new FloatPair("pr.warmup_seen", WarmupSamples), new FloatPair("pr.duration_ms", (float)DurationMs),
			new FloatPair("pr.hitch_16", _hitches[0]), new FloatPair("pr.hitch_33", _hitches[1]),
			new FloatPair("pr.hitch_50", _hitches[2]), new FloatPair("pr.hitch_100", _hitches[3]),
		};
	}
}
