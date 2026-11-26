using Halo2.Data;
using System;
using System.Diagnostics;

namespace Halo2.Combat;

// A frame profiler, on with halo_perf 1: wrap code in using var _ = HaloPerf.Measure( "Name" ).
// Once a second it logs frame time and each section's average, worst and calls per frame, most expensive first.
// Sections nest, so engine and GPU time is the frame time minus the top level sections.
// Off, a scope costs one bool test.
public static class HaloPerf {
	[ConVar( "halo2_perf", Help = "Log the frame time and the cost of the game's per-frame code once a second (see HaloPerf)." )]
	public static bool Enabled { get; set; }

	[ConVar( "halo2_perf_lines", Help = "How many sections halo_perf lists each second." )]
	public static int Lines { get; set; } = 16;

	internal sealed class Section {
		public string Name;
		public long Frame;   // ticks this frame
		public long Total;   // ticks this report
		public long Worst;   // ticks, the worst frame this report
		public int Calls;    // this report
	}

	static Dictionary<string, Section> _sections;
	static List<Section> _sorted;
	static double _reportAt = -1;
	static int _frames;
	static float _frameTotal, _frameWorst;

	// A measured section; disposing it records its time.
	public readonly struct Scope : IDisposable {
		readonly Section _section;
		readonly long _start;

		internal Scope( Section section ) {
			_section = section;
			_start = Stopwatch.GetTimestamp();
		}

		public void Dispose() {
			if ( _section is null ) return;
			var ticks = Stopwatch.GetTimestamp() - _start;
			_section.Frame += ticks;
			_section.Calls++;
		}
	}

	public static Scope Measure( string name ) {
		if ( !Enabled ) return default;

		_sections ??= new Dictionary<string, Section>();
		if ( !_sections.TryGetValue( name, out var section ) )
			_sections[name] = section = new Section { Name = name };
		return new Scope( section );
	}

	// Called first thing each frame by GamepadSystem; folds in the last frame and logs once a second.
	public static void Frame() {
		if ( !Enabled ) {
			_reportAt = -1;
			return;
		}

		_sections ??= new Dictionary<string, Section>();
		var now = RealTime.Now;
		if ( _reportAt < 0 ) {
			Reset( now );
			return;
		}

		var delta = Time.Delta;
		_frames++;
		_frameTotal += delta;
		_frameWorst = MathF.Max( _frameWorst, delta );
		foreach ( var section in _sections.Values ) {
			section.Total += section.Frame;
			if ( section.Frame > section.Worst ) section.Worst = section.Frame;
			section.Frame = 0;
		}

		if ( now < _reportAt ) return;
		Report();
		Reset( now );
	}

	static void Reset( double now ) {
		_reportAt = now + 1;
		_frames = 0;
		_frameTotal = _frameWorst = 0;
		foreach ( var section in _sections.Values ) {
			section.Frame = section.Total = section.Worst = 0;
			section.Calls = 0;
		}
	}

	static double Milliseconds( long ticks ) => ticks * 1000.0 / Stopwatch.Frequency;

	static void Report() {
		if ( _frames == 0 ) return;

		_sorted ??= new List<Section>();
		_sorted.Clear();
		foreach ( var section in _sections.Values )
			if ( section.Calls > 0 ) _sorted.Add( section );
		_sorted.Sort( static ( a, b ) => b.Total.CompareTo( a.Total ) );

		var frameMs = _frameTotal / _frames * 1000;
		Log.Info( $"halo_perf: {_frames / MathF.Max( _frameTotal, 0.0001f ):0} fps, frame {frameMs:0.00} ms avg, {_frameWorst * 1000:0.00} ms worst ({_frames} frames)" );

		var lines = Math.Clamp( Lines, 1, 200 );
		for ( var i = 0; i < _sorted.Count && i < lines; i++ ) {
			var s = _sorted[i];
			Log.Info( $"halo_perf:   {s.Name,-34} {Milliseconds( s.Total ) / _frames,7:0.000} ms avg {Milliseconds( s.Worst ),7:0.000} ms worst {s.Calls / (float)_frames,6:0.0} calls/frame" );
		}
	}
}
