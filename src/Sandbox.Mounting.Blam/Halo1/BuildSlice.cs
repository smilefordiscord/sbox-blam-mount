namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// One slice of a scene build (see <see cref="ScenarioSceneLoader.Build"/>): how long it has run,
/// and so whether it's due to yield, and which part of the build it's in, for the timing log.
/// </summary>
sealed class BuildSlice( bool stepped )
{
	/// <summary>
	/// How much work a slice does before yielding, leaving a game's frame time for drawing its
	/// loading screen at 60 frames a second.
	/// </summary>
	public const double Milliseconds = 12;

	readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();

	/// <summary>
	/// Whether the build runs a slice a frame, with frames drawn between them, rather than all at once.
	/// </summary>
	public bool Stepped { get; } = stepped;

	public string Part { get; set; } = "";

	public bool Due => _elapsed.Elapsed.TotalMilliseconds >= Milliseconds;

	public void Restart() => _elapsed.Restart();
}
