using System.Runtime.CompilerServices;
using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Lights the Halo 2 objects a game puts in a scene (models named after their object tags: bipeds, weapons, items,
/// projectiles) the way Halo 2 lights objects as they move, through each renderer's HaloObjectAmbient and the rest,
/// which halo2_opaque.shader takes when the renderer's tint isn't a row of the <see cref="ObjectLightingTable"/>.
/// </summary>
/// <remarks>
/// <para>Halo 2 keeps one lighting per object it draws, sampled straight down from the bounding sphere's centre
/// (<see cref="ObjectLightingSampler"/>) once the object has moved 0.05 world units from its last sample, and no sooner
/// than an interval that grows as the object gets smaller on screen. Drawn frame after frame, the light moves toward a
/// new sample only while the object moves, faster the faster it goes; one drawn again after a break takes it at once.
/// The light is then worked out from the sample by the map's global lighting (<see cref="ObjectLight.Evaluate"/>).</para>
///
/// <para>A unit's held weapons and first person models are lit with the unit's own lighting. A game gives renderers the
/// same HaloLightingGroup attribute (an id of 1 or more) to light them as one object: the group's unit, or else its
/// first object.</para>
/// </remarks>
sealed class MovingObjectLights
{
	public const string GroupAttribute = "HaloLightingGroup";
	const string AmbientAttribute = "HaloObjectAmbient";
	const string DirectAttribute = "HaloObjectDirect";
	const string ToDirectAttribute = "HaloObjectToDirect";
	const string IndirectAttribute = "HaloObjectIndirect";
	const string ToIndirectAttribute = "HaloObjectToIndirect";

	/// <summary>
	/// Halo 2's tick, in which its sampling intervals are counted. It moves an object's light once per frame drawn,
	/// taken here as one per tick.
	/// </summary>
	const float Tick = 1 / 60f;

	/// <summary>
	/// How far (world units) an object moves from its last sample before it's sampled again.
	/// </summary>
	const float ResampleDistance = 0.05f;

	/// <summary>
	/// An object that moves farther than this (world units) in a frame has respawned or teleported, which in Halo is a
	/// new object with lighting of its own.
	/// </summary>
	const float JumpDistance = 5;

	/// <summary>
	/// Per tick of approach at full speed: the colours' largest step, and the fraction of the angle the direction turns.
	/// </summary>
	const float ColorStep = 0.5f;
	const float DirectionStep = 0.01f;

	/// <summary>
	/// The speeds (world units per second) at which the approach starts and reaches full speed.
	/// </summary>
	const float ApproachSpeedMinimum = 0.15f;
	const float ApproachSpeedRange = 29.85f;

	sealed class Lit
	{
		public ObjectTag Object;
		public ModelRenderer Renderer;
		public bool Drawn;
		public int Frame;

		public bool HasSample;
		public bool Built;
		public System.Numerics.Vector3 SampledAt;
		public double SampledTime = double.MinValue;
		public ObjectLightSample Target;
		public System.Numerics.Vector3 Direction;
		public System.Numerics.Vector3 Lightmap;

		public System.Numerics.Vector3 Center;
		public double UpdatedTime = double.MinValue;
		public ObjectLight Light = ObjectLight.Unsampled;
		public int Version;
	}

	readonly Dictionary<object, Lit> _lit = new();
	readonly List<(ModelRenderer Renderer, Lit Lit)> _members = new();
	readonly Dictionary<ModelRenderer, (Lit Lit, int Version, int Frame)> _applied = new();
	readonly List<object> _gone = new();
	readonly List<ModelRenderer> _released = new();
	int _frame;

	/// <summary>
	/// Lights the scene's objects from its map (by name, see <see cref="SceneUpkeep"/>), as seen from the camera.
	/// </summary>
	public void Update( Scene scene, string map, CameraComponent camera )
	{
		var level = Level.Of( map );
		if ( level is null )
			return;

		_frame++;
		var now = RealTime.Now;
		var frustum = camera?.GetFrustum();

		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
		{
			if ( !renderer.IsValid() || !renderer.Active || renderer.Model is not { } model || ObjectLightingTable.IsRow( renderer.Tint ) )
				continue;

			var group = renderer.Attributes.GetFloat( GroupAttribute, 0 );
			var objectTag = level.ObjectOf( model );
			if ( group < 1 && objectTag is null )
				continue;

			object key = group >= 1 ? group : renderer;
			if ( !_lit.TryGetValue( key, out var lit ) )
				_lit[key] = lit = new Lit();

			if ( lit.Frame != _frame )
			{
				lit.Frame = _frame;
				lit.Object = null;
				lit.Renderer = null;
				lit.Drawn = false;
			}

			if ( objectTag is not null && (lit.Object is null || (!IsUnit( lit.Object ) && IsUnit( objectTag ))) )
			{
				lit.Object = objectTag;
				lit.Renderer = renderer;
			}

			lit.Drawn |= frustum is not { } f || f.IsInside( renderer.Bounds, true );
			_members.Add( (renderer, lit) );
		}

		foreach ( var (key, lit) in _lit )
		{
			if ( lit.Frame != _frame )
				_gone.Add( key );
			else if ( lit.Object is not null && lit.Drawn )
				Light( lit, level, camera, now );
		}

		foreach ( var key in _gone )
			_lit.Remove( key );
		_gone.Clear();

		// A group without its object this frame keeps what its renderers have.
		foreach ( var (renderer, lit) in _members )
		{
			if ( lit.Object is null )
				continue;

			var applied = _applied.TryGetValue( renderer, out var a ) && a.Lit == lit && a.Version == lit.Version;
			if ( !applied )
				Apply( renderer, lit.Light );
			_applied[renderer] = (lit, lit.Version, _frame);
		}
		_members.Clear();

		foreach ( var (renderer, applied) in _applied )
		{
			if ( applied.Frame != _frame )
				_released.Add( renderer );
		}

		// Renderers no longer lit here go back to the shader's own lighting.
		foreach ( var renderer in _released )
		{
			_applied.Remove( renderer );
			if ( renderer.IsValid() )
				renderer.Attributes.Set( AmbientAttribute, Vector4.Zero );
		}
		_released.Clear();
	}

	static bool IsUnit( ObjectTag objectTag ) => objectTag.Type is ObjectType.Biped or ObjectType.Vehicle;

	void Light( Lit lit, Level level, CameraComponent camera, double now )
	{
		var center = Center( lit.Renderer, lit.Object );
		var elapsed = (float)(now - lit.UpdatedTime);

		// Halo moves the light only for an object it also drew the tick before; any other takes its sample as is.
		var snap = elapsed > 2 * Tick;
		if ( !snap && System.Numerics.Vector3.Distance( center, lit.Center ) > JumpDistance )
		{
			lit.HasSample = false;
			lit.Built = false;
			lit.SampledTime = double.MinValue;
		}

		var interval = SampleInterval( ProjectedSize( lit.Object, center, camera ) ) * Tick;
		if ( !lit.HasSample || (now - lit.SampledTime > interval && System.Numerics.Vector3.Distance( center, lit.SampledAt ) >= ResampleDistance) )
		{
			lit.SampledTime = now;
			if ( level.Sampler.Sample( center, lit.Object.SearchesCardinalDirections ) is { } sample )
			{
				lit.HasSample = true;
				lit.SampledAt = center;
				lit.Target = sample with { Lightmap = Quantized( sample.Lightmap ) };
			}
		}

		if ( lit.HasSample )
		{
			if ( snap || !lit.Built )
			{
				lit.Direction = lit.Target.Direction;
				lit.Lightmap = lit.Target.Lightmap;
				lit.Built = true;
			}
			else
			{
				var speed = System.Numerics.Vector3.Distance( center, lit.Center ) / MathF.Max( elapsed, 1e-4f );
				var t = Math.Clamp( (speed - ApproachSpeedMinimum) / ApproachSpeedRange, 0, 1 );
				var ticks = elapsed / Tick;
				if ( t > 0 )
				{
					lit.Direction = ApproachDirection( lit.Direction, lit.Target.Direction, DirectionStep * t * ticks );
					lit.Lightmap = ApproachColor( lit.Lightmap, lit.Target.Lightmap, ColorStep * t * ticks );
				}
			}

			var light = ObjectLight.Evaluate( level.Lighting?.For( lit.Object.Type ), new ObjectLightSample( lit.Lightmap, lit.Direction ) );
			if ( light != lit.Light )
			{
				lit.Light = light;
				lit.Version++;
			}
		}

		lit.Center = center;
		lit.UpdatedTime = now;
	}

	/// <summary>
	/// The bounding sphere's centre (world units): the object's bounding offset from its root node, which for a biped is
	/// its pelvis, not its feet.
	/// </summary>
	static System.Numerics.Vector3 Center( ModelRenderer renderer, ObjectTag objectTag )
	{
		var root = renderer is SkinnedModelRenderer { SceneModel: { } model } && model.IsValid() && renderer.Model is { BoneCount: > 0 }
			? model.GetBoneWorldTransform( 0 )
			: renderer.WorldTransform;
		return (System.Numerics.Vector3)(root.PointToWorld( (Vector3)objectTag.BoundingOffset * Halo2Mount.WorldScale ) / Halo2Mount.WorldScale);
	}

	/// <summary>
	/// Ticks between samples by the object's size on screen (pixels across its bounding radius).
	/// </summary>
	static int SampleInterval( float size ) => size switch
	{
		> 400 => 6,
		> 200 => 12,
		> 100 => 18,
		> 50 => 30,
		> 10 => 90,
		_ => 120,
	};

	/// <summary>
	/// The bounding radius in pixels at the centre's depth in view, which Halo keeps at least 0.1 world units.
	/// </summary>
	static float ProjectedSize( ObjectTag objectTag, System.Numerics.Vector3 center, CameraComponent camera )
	{
		if ( camera is null )
			return 0;

		var eye = camera.WorldPosition / Halo2Mount.WorldScale;
		var depth = MathF.Max( MathF.Abs( Vector3.Dot( (Vector3)center - eye, camera.WorldRotation.Forward ) ), 0.1f );
		var pixelsPerUnit = Screen.Width * 0.5f / MathF.Tan( camera.FieldOfView.DegreeToRadian() * 0.5f );
		return objectTag.BoundingRadius / depth * pixelsPerUnit;
	}

	/// <summary>
	/// Halo keeps a sample's colours as bytes, truncating each time it moves them.
	/// </summary>
	static System.Numerics.Vector3 Quantized( System.Numerics.Vector3 color ) =>
		new( Byte( color.X ), Byte( color.Y ), Byte( color.Z ) );

	static float Byte( float value ) => (int)(Math.Clamp( value, 0, 1 ) * 255f) * (1 / 255f);

	// Each channel by at most step.
	static System.Numerics.Vector3 ApproachColor( System.Numerics.Vector3 color, System.Numerics.Vector3 target, float step ) =>
		Quantized( color + System.Numerics.Vector3.Clamp( target - color, new( -step ), new( step ) ) );

	/// <summary>
	/// Turns a direction a fraction of the way toward another and moves its length (how directional the light is) by at
	/// most that fraction; nearly parallel ones move each component by at most it.
	/// </summary>
	static System.Numerics.Vector3 ApproachDirection( System.Numerics.Vector3 direction, System.Numerics.Vector3 target, float fraction )
	{
		var length = Math.Clamp( direction.Length(), 1e-4f, 10 );
		var targetLength = Math.Clamp( target.Length(), 1e-4f, 10 );
		var from = direction / length;
		var to = target / targetLength;
		var cosine = System.Numerics.Vector3.Dot( from, to );
		var axis = System.Numerics.Vector3.Cross( from, to );
		if ( MathF.Abs( cosine ) >= 0.9999f || length <= 1e-4f || targetLength <= 1e-4f || axis.LengthSquared() < 1e-12f )
			return direction + System.Numerics.Vector3.Clamp( target - direction, new( -fraction ), new( fraction ) );

		var turned = System.Numerics.Vector3.Transform( from, System.Numerics.Quaternion.CreateFromAxisAngle( System.Numerics.Vector3.Normalize( axis ), MathF.Acos( cosine ) * fraction ) );
		return turned * (length + Math.Clamp( targetLength - length, -fraction, fraction ));
	}

	static void Apply( ModelRenderer renderer, ObjectLight light )
	{
		var attributes = renderer.Attributes;
		attributes.Set( AmbientAttribute, new Vector4( (Vector3)light.Ambient, 1 ) );
		attributes.Set( DirectAttribute, (Vector3)light.Direct );
		attributes.Set( ToDirectAttribute, (Vector3)light.ToDirect );
		attributes.Set( IndirectAttribute, (Vector3)light.Indirect );
		attributes.Set( ToIndirectAttribute, (Vector3)light.ToIndirect );
	}

	/// <summary>
	/// A map's lightmap sampler, global lighting and object tags by path, kept per map once built.
	/// </summary>
	sealed class Level
	{
		static readonly Dictionary<string, Level> Levels = new( StringComparer.OrdinalIgnoreCase );

		public ObjectLightingSampler Sampler { get; private init; }
		public GlobalLightingTag Lighting { get; private init; }
		Dictionary<string, TagEntry> _objects;
		readonly ConditionalWeakTable<Model, ObjectTagOf> _found = new();

		sealed record ObjectTagOf( ObjectTag Object );

		// Built once a map's cache is open; a map that fails stays unlit.
		public static Level Of( string map )
		{
			if ( map is null )
				return null;

			if ( Levels.TryGetValue( map, out var level ) )
				return level;

			if ( Halo2Mount.Current is not { } mount || mount.GetMap( map ) is not { } cache )
				return null;

			Levels[map] = level = Build( mount, cache, map );
			return level;
		}

		static Level Build( Halo2Mount mount, CacheFile cache, string map )
		{
			try
			{
				var scenario = new ScenarioTag( cache.Scenario );
				var bsps = scenario.StructureBsps.Where( b => b.Bsp is not null )
					.Select( b => (Bsp: new StructureBspTag( b.Bsp ), Lightmap: b.Lightmap is null ? null : new LightmapTag( b.Lightmap )) )
					.ToList();

				var objects = new Dictionary<string, TagEntry>( StringComparer.OrdinalIgnoreCase );
				foreach ( var tag in cache.Tags )
				{
					if ( !string.IsNullOrEmpty( tag.Path ) && ObjectTag.IsObject( tag ) )
						objects.TryAdd( tag.Path, tag );
				}

				return new Level
				{
					Sampler = new ObjectLightingSampler( bsps, ObjectLightingSampler.TexturePages( mount.Textures ) ),
					Lighting = GlobalLightingTag.For( cache, new ScenarioObjects( cache.Scenario ).GlobalLighting ),
					_objects = objects,
				};
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"{map}: couldn't light moving objects: {e.Message}" );
				return null;
			}
		}

		/// <summary>
		/// The object a model is named after (mount://halo2/&lt;object tag path&gt;.vmdl), or null.
		/// </summary>
		public ObjectTag ObjectOf( Model model )
		{
			if ( _found.TryGetValue( model, out var found ) )
				return found.Object;

			var name = model.Name ?? "";
			var scheme = name.IndexOf( "://", StringComparison.Ordinal );
			if ( scheme >= 0 )
			{
				name = name[(scheme + 3)..];
				var slash = name.IndexOf( '/' );
				name = slash >= 0 ? name[(slash + 1)..] : "";
			}

			if ( name.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
				name = name[..^5];

			var objectTag = _objects.TryGetValue( name.Replace( '/', '\\' ), out var tag ) ? new ObjectTag( tag ) : null;
			_found.AddOrUpdate( model, new ObjectTagOf( objectTag ) );
			return objectTag;
		}
	}
}
