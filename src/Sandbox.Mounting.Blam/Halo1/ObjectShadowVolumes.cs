using System.Reflection;
using Sandbox.Internal;
using Blam.Halo1;

namespace Sandbox.Mounting.Blam.Halo1;

// Hands halo1_environment.shader the nearest shadow casters' bounding spheres each frame, so their shadows stop where Halo's did instead of falling through floors.
// Added to live scenes of a Halo CE level the way MirrorReflections is.
sealed class ObjectShadowVolumes : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	const string CastersAttribute = "HaloShadowCasters";
	const string CountAttribute = "HaloShadowCasterCount";

	// The most casters the shader looks through, nearest the camera first.
	const int MaximumCasters = 256;

	// How many frames the renderers found are used for before looking again.
	const int RefreshFrames = 30;

	// The renderers found, with their model's shape when they were found.
	readonly List<(ModelRenderer Renderer, Model Model, ObjectLightingShape Shape)> _renderers = new();

	// This frame's casters, and their distances from the camera (squared).
	Vector4[] _casters = new Vector4[MaximumCasters];
	float[] _distances = new float[MaximumCasters];
	GpuBuffer<Vector4> _buffer;
	int _framesUntilRefresh;

	protected override void OnDisabled()
	{
		Scene.RenderAttributes.Set( CountAttribute, -1 );
		Scene.RenderAttributes.Set( CastersAttribute, (GpuBuffer)null );
		_buffer?.Dispose();
		_buffer = null;
		_renderers.Clear();
	}

	// IPreRenderSubscriber is what gets OnPreRender called (see MirrorReflections).
	protected override void OnPreRender()
	{
		if ( --_framesUntilRefresh <= 0 )
		{
			_renderers.Clear();
			foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
			{
				if ( renderer.RenderType != ModelRenderer.ShadowRenderType.Off && renderer.Model is { } model )
					_renderers.Add( (renderer, model, MovingObjectLights.ShapeOf( model )) );
			}
			_framesUntilRefresh = RefreshFrames;
		}

		var camera = Scene.Camera is { } main && main.IsValid() && main.Enabled
			? main
			: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );
		var eye = camera?.WorldPosition ?? Vector3.Zero;

		var count = 0;
		foreach ( var (renderer, found, foundShape) in _renderers )
		{
			// A culled renderer casts no shadow.
			if ( !renderer.IsValid() || !renderer.Active || renderer.RenderType == ModelRenderer.ShadowRenderType.Off || renderer.Model is not { } model || RenderCulling.IsCulled( renderer ) )
				continue;

			Vector3 center;
			float radius;
			if ( (model == found ? foundShape : MovingObjectLights.ShapeOf( model )) is { } shape )
			{
				center = MovingObjectLights.Center( renderer, shape );
				radius = shape.Radius * HaloCeMount.WorldScale * renderer.WorldScale.x;
			}
			else
			{
				// Not a Halo model (a game's own): its bounds' sphere.
				var bounds = renderer.Bounds;
				center = bounds.Center;
				radius = bounds.Size.Length * 0.5f;
			}

			if ( radius <= 0 )
				continue;

			if ( count == _casters.Length )
			{
				Array.Resize( ref _casters, count * 2 );
				Array.Resize( ref _distances, count * 2 );
			}
			_casters[count] = new Vector4( center, radius );
			_distances[count] = center.DistanceSquared( eye );
			count++;
		}

		// The shader takes the strongest of the casters it's given, so only which are nearest matters, not their order.
		if ( count > MaximumCasters )
		{
			SelectNearest( count, MaximumCasters );
			count = MaximumCasters;
		}

		if ( _buffer is null || _buffer.ElementCount < Math.Max( count, 1 ) )
		{
			_buffer?.Dispose();
			_buffer = new GpuBuffer<Vector4>( Math.Max( count, 64 ), GpuBuffer.UsageFlags.Structured, "halo shadow casters" );
		}

		if ( count > 0 )
			_buffer.SetData( _casters.AsSpan( 0, count ) );

		Scene.RenderAttributes.Set( CastersAttribute, _buffer );
		Scene.RenderAttributes.Set( CountAttribute, count );
	}

	/// <summary>Moves the <paramref name="nearest"/> nearest of the first <paramref name="count"/> casters to the front.</summary>
	void SelectNearest( int count, int nearest )
	{
		var low = 0;
		var high = count - 1;
		while ( low < high )
		{
			var pivot = _distances[(low + high) >> 1];
			var i = low;
			var j = high;
			while ( i <= j )
			{
				while ( _distances[i] < pivot )
					i++;
				while ( _distances[j] > pivot )
					j--;
				if ( i <= j )
				{
					(_distances[i], _distances[j]) = (_distances[j], _distances[i]);
					(_casters[i], _casters[j]) = (_casters[j], _casters[i]);
					i++;
					j--;
				}
			}

			if ( nearest - 1 <= j )
				high = j;
			else if ( nearest - 1 >= i )
				low = i;
			else
				return;
		}
	}

	// Watching for scenes of a Halo CE level (see MirrorReflections).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	// The hidden object holding a scene's shadow volumes, looked for by name (see MirrorReflections).
	const string ObjectName = "halo object shadow volumes";

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
				if ( !scene.IsValid() || scene.Children.Any( g => g.Name == ObjectName ) || MovingObjectLights.MapOf( scene ) is null )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo object shadow volumes: {e.Message}" );
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
			var volumes = go.Components.Create<ObjectShadowVolumes>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			volumes.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			volumes.Enabled = true;
		}
	}
}
