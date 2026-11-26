using System.Reflection;
using Sandbox.Internal;
using Blam.Halo1;

namespace Sandbox.Mounting.Blam.Halo1;

// Halo CE's particles, particle systems and effect lights, simulated and drawn the way Halo does it. Particles belong to the world, so they outlive the effect that made them unless they stay attached.
sealed partial class HaloParticleWorld : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	// One of Halo's game ticks, which effects run at.
	const float Tick = 1 / 30f;

	// Longest step a frame takes: a hitch doesn't throw particles through walls.
	const float MaxFrameTime = 0.1f;

	const float Units = HaloCeMount.WorldScale;

	readonly Random _random = new();
	readonly List<TemporaryEffect> _markers = new();
	readonly HashSet<GameObject> _notEffects = new();

	// Roots of effects that have played out; a root plays again only once it's been turned off and on (a game's pool).
	// Only the root's own switch counts: hiding what carries it (a view model while zoomed) leaves it played.
	readonly HashSet<GameObject> _finished = new();
	readonly List<GameObject> _replayable = new();
	float _tickTime;

	// Time the last frame's update and the last view's draw took, for checking the cost.
	internal float UpdateMilliseconds;
	internal float DrawMilliseconds;
	internal int DrawRuns;
	internal int DrawQuads;
	float _now;
	int _frame;

	float Rand() => _random.NextSingle();

	// Between lower and upper, in either order.
	float Range( float lower, float upper ) => lower + (upper - lower) * Rand();

	// A whole number in [lower, upper), or lower if that's empty.
	int RangeInt( int lower, int upper ) => upper > lower ? _random.Next( lower, upper ) : lower;

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
		MakeClampMaterials();
	}

	protected override void OnDisabled()
	{
		foreach ( var light in _lights )
			light.Destroy();
		_lights.Clear();
		_effects.Clear();
		_effectsByRoot.Clear();
		_systems.Clear();
		ClearParticles();
		ClearRender();
	}

	// Runs after the components' updates, so an effect spawned this frame starts this frame.
	protected override void OnPreRender()
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		var dt = Math.Clamp( Time.Delta, 0, MaxFrameTime );
		_now += dt;
		_frame++;
		Traces = 0;

		try
		{
			Discover();
			UpdateWater();

			// Effects run at Halo's tick; particles and systems move every frame.
			_tickTime += dt;
			while ( _tickTime >= Tick )
			{
				_tickTime -= Tick;
				UpdateEffects( Tick );
			}

			UpdateParticles( dt );
			UpdateSystems( dt );
			UpdateLights( dt );
			Snapshot();
			UpdateMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Halo particles: {e.Message}" );
		}
	}

	// Starts effects spawned since the last frame: enabled TemporaryEffects on halo_effect roots.
	void Discover()
	{
		_markers.Clear();
		Scene.GetAll( _markers );
		foreach ( var marker in _markers )
		{
			var go = marker.GameObject;
			if ( go is null || _effectsByRoot.ContainsKey( go ) || _systemsByRoot.ContainsKey( go ) || _notEffects.Contains( go ) || _finished.Contains( go ) )
				continue;

			if ( !go.Tags.Has( HaloEffects.RootTag, false ) || !Start( go ) )
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

	// Starts what a root's token names. False if it names nothing that plays.
	bool Start( GameObject root, Vector3 velocity = default )
	{
		string token = null;
		foreach ( var tag in root.Tags.TryGetAll() )
		{
			if ( HaloEffectLibrary.IsToken( tag ) && root.Tags.Has( tag, false ) )
			{
				token = tag;
				break;
			}
		}

		if ( token is null )
			return false;

		switch ( HaloEffectLibrary.Get( token ) )
		{
			case HaloEffectDefinition effect:
				StartEffect( effect, root, velocity );
				return true;

			case HaloSystemDefinition system:
				StartAttachedSystem( system, root );
				return true;

			default:
				return false;
		}
	}

	// The child a game drives an effect through (see ControlsName).
	static GameObject FindControls( GameObject root )
	{
		foreach ( var child in root.Children )
		{
			if ( child.Name == HaloEffects.ControlsName )
				return child;
		}

		return null;
	}

	// Halo's scales A and B and change color from an effect's controls (1, 1 and white without them).
	static (float A, float B, Vector3 Color) ReadControls( GameObject controls )
	{
		if ( controls is null || !controls.IsValid() )
			return (1, 1, Vector3.One);

		var p = controls.LocalPosition;
		var s = controls.LocalScale;
		return (Math.Clamp( p.x, 0, 1 ), Math.Clamp( p.y, 0, 1 ), new Vector3( Math.Clamp( s.x, 0, 1 ), Math.Clamp( s.y, 0, 1 ), Math.Clamp( s.z, 0, 1 ) ));
	}

	// Tracks how fast an object moves (world units per second), from where it was when last measured.
	sealed class Carrier
	{
		public GameObject Object;
		public Vector3 Position;
		public float Time = -1;
		public Vector3 Velocity;

		// Faster than this (world units per second) is a jump (a teleport, a respawn), not movement.
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

		// What carries an effect: the nearest halo_attachments root above it, else its own root.
		public static Carrier For( GameObject root )
		{
			var carrier = root;
			for ( var go = root; go is not null && go is not Sandbox.Scene; go = go.Parent )
			{
				if ( go.Tags.Has( "halo_attachments", false ) )
				{
					carrier = go;
					break;
				}
			}

			return new Carrier { Object = carrier };
		}
	}

	// Watching for scenes to play Halo effects in (see MovingObjectLights).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	const string ObjectName = "halo particles";

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	// Starts looking for live scenes with a Halo level or Halo effects, twice a second. Safe to call more than once.
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
				if ( !scene.IsValid() || scene is PrefabScene || scene.Children.Any( g => g.Name == ObjectName ) || !Wanted( scene ) )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo particles: {e.Message}" );
		}
	}

	// A scene with a Halo CE level or Halo effects.
	static bool Wanted( Scene scene ) =>
		scene.FindAllWithTag( MovingObjectLights.MapTag ).Any() || scene.FindAllWithTag( HaloEffects.RootTag ).Any();

	static void AddTo( Scene scene )
	{
		using ( scene.Push() )
		{
			var go = new GameObject( true, ObjectName );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

			// As in MirrorReflections: the game's type library doesn't know mount types.
			go.Flags |= GameObjectFlags.Deserializing;
			var world = go.Components.Create<HaloParticleWorld>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			world.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			world.Enabled = true;
		}
	}
}
