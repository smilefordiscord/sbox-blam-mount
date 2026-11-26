using System.Reflection;
using Sandbox.Internal;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's effects and particles, run by the mount itself (the user's call for Halo 2, kept for Halo 3): effects and particles step at Halo's 30 Hz tick and are drawn between their last two ticks.
// Prefabs and scenes only carry engine components, so an effect root is marked (Halo3ParticleEffects) and this, added to the scene by Watch, plays it. The rules follow MCC's particle compute shaders and the tags.
sealed partial class Halo3ParticleWorld : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	const float Tick = 1 / 30f;

	// Longest a frame catches up (seconds of real time, in 30 Hz ticks). The editor renders on demand, so the time since its last frame is real time the effects should have run.
	const float MaxFrameTime = 1f;

	readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
	double _lastFrame = -1;

	const float Units = Halo3Mount.WorldScale;

	readonly Random _random = new();
	readonly List<TemporaryEffect> _markers = new();
	readonly HashSet<GameObject> _notEffects = new();

	// Roots of one-shot effects that have played out; one plays again once it's been turned off and on (a game's pool).
	readonly HashSet<GameObject> _finished = new();
	readonly List<GameObject> _replayable = new();

	float _tickTime;
	float _now;
	int _frame;

	// Seconds of game time, Halo's "game time" input.
	float _gameTime;

	// What the last frame cost (milliseconds) and drew, for checking.
	internal float UpdateMilliseconds;
	internal int ParticleCount;

	float Rand() => _random.NextSingle();

	float Range( float lower, float upper ) => lower + (upper - lower) * Rand();

	// A random direction, uniform over the sphere.
	Vector3 RandomDirection()
	{
		var z = Range( -1, 1 );
		var a = Rand() * MathF.PI * 2;
		var r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		return new Vector3( r * MathF.Cos( a ), r * MathF.Sin( a ), z );
	}

	protected override void OnEnabled()
	{
		ClearRender();
		MakeCameraProbe();
	}

	protected override void OnDisabled()
	{
		// The draw objects first: the lists they hang off are cleared after.
		ClearRender();
		_effects.Clear();
		_effectsByRoot.Clear();
		_systems.Clear();
		_particleCount = 0;
		_volumes.Clear();
		_volumesByKey.Clear();
	}

	// After the components' updates, so an effect spawned this frame starts this frame.
	protected override void OnPreRender()
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();

		// Real time, not Time.Delta: in the editor frames come only when something redraws.
		var clock = _clock.Elapsed.TotalSeconds;
		var dt = _lastFrame < 0 ? 0 : (float)Math.Clamp( clock - _lastFrame, 0, MaxFrameTime );
		_lastFrame = clock;
		_now += dt;
		_frame++;

		try
		{
			Discover();
			UpdateCameras();

			_tickTime += dt;
			while ( _tickTime >= Tick )
			{
				_tickTime -= Tick;
				_gameTime += Tick;
				_ticks++;
				UpdateEffects( Tick );
				UpdateSystems( Tick );
			}

			Snapshot( _tickTime / Tick );
			WriteDistortion();
			UpdateMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Halo 3 particles: {e.Message}" );
		}
	}

	// Starts effects spawned since the last frame: enabled TemporaryEffects on marked roots.
	void Discover()
	{
		_markers.Clear();
		Scene.GetAll( _markers );
		foreach ( var marker in _markers )
		{
			var go = marker.GameObject;
			if ( go is null || _effectsByRoot.ContainsKey( go ) || _notEffects.Contains( go ) || _finished.Contains( go ) )
				continue;

			if ( !go.Tags.Has( Halo3ParticleEffects.RootTag, false ) || !Start( go ) )
				_notEffects.Add( go );
		}

		foreach ( var root in _finished )
		{
			if ( !root.IsValid() || !root.Active )
				_replayable.Add( root );
		}
		foreach ( var root in _replayable )
			_finished.Remove( root );
		_replayable.Clear();

		if ( _frame % 300 == 0 )
			_notEffects.RemoveWhere( g => !g.IsValid() );
	}

	// Starts the effect a root's token names; false if it names none.
	bool Start( GameObject root )
	{
		var tags = root.Tags.TryGetAll().ToList();
		var map = tags.FirstOrDefault( t => t.StartsWith( Halo3ParticleEffects.MapTagPrefix, StringComparison.Ordinal ) )?[Halo3ParticleEffects.MapTagPrefix.Length..];
		foreach ( var tag in tags )
		{
			if ( Halo3ParticleLibrary.IsToken( tag ) && Halo3ParticleLibrary.Get( tag, map ) is { } effect )
			{
				StartEffect( effect, root, null );
				return true;
			}
		}

		return false;
	}

	// Halo's effect scales A and B from an effect's controls (1 and 1 without them).
	static (float A, float B) ReadControls( GameObject controls )
	{
		if ( controls is null || !controls.IsValid() )
			return (1, 1);

		var p = controls.LocalPosition;
		return (Math.Clamp( p.x, 0, 1 ), Math.Clamp( p.y, 0, 1 ));
	}

	static GameObject FindControls( GameObject root )
	{
		foreach ( var child in root.Children )
		{
			if ( child.Name == Halo3ParticleEffects.ControlsName )
				return child;
		}

		return null;
	}

	// Tracks how fast an effect's carrier moves (world units per second), for systems that inherit the effect's velocity.
	sealed class Carrier
	{
		public GameObject Object;
		public Vector3 Position;
		public float Time = -1;
		public Vector3 Velocity;

		// Faster than this (world units per second) is a jump, not movement.
		const float MaximumSpeed = 50;

		public void Measure( float now )
		{
			if ( !Object.IsValid() )
			{
				Velocity = Vector3.Zero;
				return;
			}

			var position = Object.WorldPosition / Units;
			var elapsed = now - Time;
			if ( Time >= 0 && elapsed > 1e-4f )
			{
				var velocity = (position - Position) / elapsed;
				Velocity = velocity.Length <= MaximumSpeed ? velocity : Vector3.Zero;
			}

			if ( Time < 0 || elapsed > 1e-4f )
			{
				Position = position;
				Time = now;
			}
		}
	}

	// Watching for scenes with Halo 3 effects (as Halo 2's runtime does).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	const string ObjectName = "halo 3 particles";

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	// Looks for live scenes with Halo 3 effects twice a second. Safe to call more than once.
	public static void Watch()
	{
		lock ( WatchLock )
		{
			_watchTimer ??= new System.Threading.Timer( _ => QueueScan(), null, 0, 500 );
		}
	}

	public static void StopWatching()
	{
		lock ( WatchLock )
		{
			_watchTimer?.Dispose();
			_watchTimer = null;
		}
	}

	static void QueueScan()
	{
		if ( System.Threading.Interlocked.Exchange( ref _scanQueued, 1 ) == 1 )
			return;

		MainThread.Queue( Scan );
	}

	static void Scan()
	{
		_scanQueued = 0;
		try
		{
			var scenes = AllScenesProperty?.GetValue( null ) is System.Collections.IEnumerable all ? all.OfType<Scene>().ToList() : Scene.All.ToList();
			foreach ( var scene in scenes )
			{
				if ( !scene.IsValid() || scene is PrefabScene || scene.Children.Any( g => g.Name == ObjectName ) || !scene.FindAllWithTag( Halo3ParticleEffects.RootTag ).Any() )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't add Halo 3 particles: {e.Message}" );
		}
	}

	static void AddTo( Scene scene )
	{
		using ( scene.Push() )
		{
			var go = new GameObject( true, ObjectName );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

			// As in MirrorReflections: the game's type library doesn't know mount types.
			go.Flags |= GameObjectFlags.Deserializing;
			var world = go.Components.Create<Halo3ParticleWorld>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			world.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			world.Enabled = true;
		}
	}
}
