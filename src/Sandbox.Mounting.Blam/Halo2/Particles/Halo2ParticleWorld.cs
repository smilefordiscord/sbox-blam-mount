using System.Reflection;
using Sandbox.Internal;

namespace Sandbox.Mounting.Blam.Halo2;

// Halo 2's effects and particles, run the way Halo 2 runs them: effects and particles step at Halo's 30 Hz tick and are drawn between their last two ticks.
// Prefabs and scenes only carry engine components, so an effect root is marked (Halo2ParticleEffects) and this, added to the scene by Watch, plays it.
sealed partial class Halo2ParticleWorld : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	const float Tick = 1 / 30f;

	// Longest a frame steps: a hitch doesn't throw particles through walls or run hundreds of ticks.
	const float MaxFrameTime = 0.1f;

	const float Units = Halo2Mount.WorldScale;

	readonly Random _random = new();
	readonly List<TemporaryEffect> _markers = new();
	readonly HashSet<GameObject> _notEffects = new();

	// Roots of one-shot effects that have played out; one plays again once it's been turned off and on (a game's pool).
	// Only the root's own switch counts: hiding what carries it (a view model while zoomed) leaves it played.
	readonly HashSet<GameObject> _finished = new();
	readonly List<GameObject> _replayable = new();

	float _tickTime;
	float _now;
	int _frame;

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
		_effects.Clear();
		_effectsByRoot.Clear();
		_systems.Clear();
		_particleCount = 0;
		ClearRender();
		ClearDecals();
		DeleteDecalLayers();
	}

	// After the components' updates, so an effect spawned this frame starts this frame.
	protected override void OnPreRender()
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		var dt = Math.Clamp( Time.Delta, 0, MaxFrameTime );
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
				UpdateEffects( Tick );
				UpdateSystems( Tick );
				UpdateDecals();
			}

			Snapshot( _tickTime / Tick );
			SnapshotDecals();
			UpdateMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( e, $"Halo 2 particles: {e.Message}" );
		}
	}

	// Seconds of game time, Halo's "game time" input.
	float _gameTime;

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

			if ( !go.Tags.Has( Halo2ParticleEffects.RootTag, false ) || !Start( go ) )
				_notEffects.Add( go );
		}

		foreach ( var root in _finished )
		{
			if ( !root.IsValid() || !root.Enabled )
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
		foreach ( var tag in root.Tags.TryGetAll() )
		{
			if ( Halo2ParticleLibrary.IsToken( tag ) && Halo2ParticleLibrary.Get( tag ) is { } effect )
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

	static GameObject FindChild( GameObject root, string name )
	{
		foreach ( var child in root.Children )
		{
			if ( child.Name == name )
				return child;
		}

		return null;
	}

	// Tracks how fast an effect's carrier moves (world units per second), for effects that pass their velocity on.
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

	// Watching for scenes with Halo 2 effects (as Halo CE's HaloParticleWorld does).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	const string ObjectName = "halo 2 particles";

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	// Looks for live scenes with Halo 2 effects twice a second. Safe to call more than once.
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
				if ( !scene.IsValid() || scene is PrefabScene || scene.Children.Any( g => g.Name == ObjectName ) || !scene.FindAllWithTag( Halo2ParticleEffects.RootTag ).Any() )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( e, $"Couldn't add Halo 2 particles: {e.Message}" );
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
			var world = go.Components.Create<Halo2ParticleWorld>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			world.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			world.Enabled = true;
		}
	}
}
