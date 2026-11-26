using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// Decals effects leave (bullet holes, scorches, blood), made, kept and faded as Halo 2 does.
// They're laid onto the level's collision surfaces (DecalGeometry) and drawn as its placed decals are, a layer at a time.
sealed partial class Halo2ParticleWorld
{
	// Halo's decal layers that draw (the error layer doesn't).
	public const int DecalLayers = 6;

	sealed class LiveDecal
	{
		public PlacedDecal Placed;
		public Material Material;
		public DecalShaderVertex[] Vertices;
		public int Layer;
		public int CreatedTick;

		// The first decal of its chain, itself for the first.
		public LiveDecal Head;

		// Kept when the decal memory runs short (about a tenth of the chains), until too many are.
		public bool Kept;

		public byte Alpha = 255;
	}

	// Oldest first.
	readonly List<LiveDecal> _decals = new();
	readonly Dictionary<string, Material> _decalMaterials = new();
	readonly DecalGeometry.HaloRandom _decalRandom = new( (uint)Environment.TickCount );
	int _decalTicks;
	int _decalVertices;

	// Halo's count of kept decals, which also counts (and never uncounts) the later decals of kept chains.
	int _keptDecals;

	string _decalMap;
	bool _decalLevelLooked;
	List<DecalGeometry> _decalLevel;

	// Halo 2's decal limits: 1024 decals, and its decal vertex memory (0x13FE pages of one 32 byte vertex each).
	const int MaxDecals = 1024;
	const int MaxDecalVertices = 0x13FE;

	// Kept decals past this many are let go, at random, until half are left.
	const int MaxKeptDecals = 256;

	// Out of 65536: the chance a new chain is kept (0x9FFF6 / 100), and that a kept one is let go when there are too many (0x28FFD7 / 100).
	const float KeepChance = 6553.5f;
	const float LetGoChance = 26869.35f;
	const int MaxLetGoPasses = 100;

	// Part flags: aim straight down whatever the location's direction, and make the decal however far the views are.
	const int FaceDownFlag = 0x1;
	const int AnyDistanceFlag = 0x10;

	// Beyond this from every view (world units, times the view's field of view in radians), only one decal part in four is made.
	const float DecalViewDistance = 20;
	const float FarDecalChance = 0.25f;

	float DecalRandom() => _decalRandom.NextFraction();

	// An event's decal parts at each place of their locations, as its duration starts.
	void PlaceDecals( EffectInstance effect, Halo2EventDefinition ev )
	{
		foreach ( var decal in ev.Decals )
		{
			var part = decal.Part;

			// Water and space only parts never play here, nor nonviolent only ones; the rest play in air.
			if ( part.Environment is 2 or 3 || part.Disposition == 2 )
				continue;
			if ( part.Location < 0 || part.Location >= effect.Locations.Length )
				continue;

			foreach ( var place in effect.Locations[part.Location] )
			{
				if ( !place.Valid )
					continue;

				var (position, rotation) = place.Frame;
				if ( (part.Flags & AnyDistanceFlag) == 0 && !NearView( position ) && DecalRandom() >= FarDecalChance )
					continue;

				var forward = (part.Flags & FaceDownFlag) != 0 ? Vector3.Down : rotation.Forward;
				var velocity = PartVelocity( effect, part, forward );
				var scale = part.RadiusModifier.X + (part.RadiusModifier.Y - part.RadiusModifier.X) * DecalRandom();
				NewDecal( decal.Decal, position, velocity, scale );
			}
		}
	}

	bool NearView( Vector3 position )
	{
		var fov = ViewFieldOfView();
		foreach ( var camera in _cameras )
		{
			if ( camera.Distance( position ) * fov < DecalViewDistance )
				return true;
		}

		return false;
	}

	// The scene camera's horizontal field of view in radians, as Halo's view keeps it.
	float ViewFieldOfView()
	{
		if ( Scene.Camera is not { } camera )
			return 70f.DegreeToRadian();

		var fov = camera.FieldOfView.DegreeToRadian();
		if ( camera.FovAxis == CameraComponent.Axis.Vertical )
			fov = 2 * MathF.Atan( MathF.Tan( fov / 2 ) * Screen.Aspect );
		return fov;
	}

	// The part's speed between its bounds along forward turned by up to its cone, each scaled by the effect's A and B scales where its flags say.
	Vector3 PartVelocity( EffectInstance effect, EffectPart part, Vector3 forward )
	{
		float Scaled( float value, int bit ) => value * ((part.AScales & bit) != 0 ? effect.A : 1) * ((part.BScales & bit) != 0 ? effect.B : 1);

		var speed = Scaled( part.Velocity.X, 1 ) + DecalRandom() * Scaled( part.Velocity.Y - part.Velocity.X, 2 );
		var angle = DecalRandom() * Scaled( part.VelocityCone, 4 );
		var direction = forward;
		if ( angle != 0 )
		{
			var axis = DecalRandomDirection();
			var (sin, cos) = MathF.SinCos( angle );
			direction = cos * direction + Vector3.Dot( axis, direction ) * (1 - cos) * axis - sin * direction.Cross( axis );
		}

		return direction * speed;
	}

	Vector3 DecalRandomDirection()
	{
		var z = DecalRandom() * 2 - 1;
		var a = DecalRandom() * MathF.PI * 2;
		var r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		return new Vector3( r * MathF.Cos( a ), r * MathF.Sin( a ), z );
	}

	// A decal (and its chain) where a ray from position along velocity (world units) meets the level.
	void NewDecal( DecalTag decal, Vector3 position, Vector3 velocity, float scale )
	{
		if ( DecalLevel() is not { } level )
			return;

		foreach ( var geometry in level )
		{
			var placed = geometry.Place( decal, position, velocity, scale, _decalRandom, BitmapOf, NotOverlapping );
			if ( placed.Count == 0 )
				continue;

			LiveDecal head = null;
			foreach ( var p in placed )
			{
				var live = AddDecal( p, head );
				if ( live is null )
					break;
				head ??= live;
			}

			return;
		}
	}

	readonly Dictionary<TagEntry, BitmapTag> _decalBitmaps = new();

	BitmapTag BitmapOf( DecalTag decal )
	{
		if ( decal.Bitmap is null || decal.Bitmap.Class != BitmapTag.Class )
			return null;
		if ( !_decalBitmaps.TryGetValue( decal.Bitmap, out var bitmap ) )
			_decalBitmaps[decal.Bitmap] = bitmap = new BitmapTag( decal.Bitmap );
		return bitmap;
	}

	// Halo's ring of recent decals: a decal is refused where its tag's maximum overlapping count of them already lie close by.
	readonly (DecalTag Tag, Vector3 Position, float Radius, float Expires, int Stamp)[] _recentDecals = new (DecalTag, Vector3, float, float, int)[24];
	int _recentStamp;

	bool NotOverlapping( DecalTag decal, System.Numerics.Vector3 point )
	{
		if ( decal.MaximumOverlapping <= 0 )
			return true;

		var position = (Vector3)point;
		var rejection = decal.OverlapRejection == 0 ? 1 : decal.OverlapRejection;
		var now = _decalTicks * Tick;
		var overlapping = 0;
		foreach ( var recent in _recentDecals )
		{
			if ( recent.Tag?.Tag != decal.Tag || (recent.Expires != 0 && recent.Expires <= now) || recent.Radius <= 0 )
				continue;

			if ( recent.Position.Distance( position ) <= rejection * decal.RadiusMax + recent.Radius && ++overlapping >= decal.MaximumOverlapping )
				return false;
		}

		// Into a free or expired entry, else over the oldest.
		var slot = 0;
		for ( var i = 0; i < _recentDecals.Length; i++ )
		{
			var recent = _recentDecals[i];
			if ( recent.Tag is null || (recent.Expires > 0 && now > recent.Expires) )
			{
				slot = i;
				break;
			}

			if ( recent.Stamp <= _recentDecals[slot].Stamp )
				slot = i;
		}

		_recentDecals[slot] = (decal, position, rejection * decal.RadiusMax, decal.LifetimeMax == 0 ? 0 : now + decal.LifetimeMax, ++_recentStamp);
		return true;
	}

	// Null if Halo would refuse it: no triangles, no material, or no room.
	LiveDecal AddDecal( PlacedDecal placed, LiveDecal head )
	{
		var count = placed.Triangles.Count;
		if ( count == 0 || placed.Decal.Layer == DecalLayer.Error || DecalMaterialOf( placed ) is not { } material )
			return null;
		if ( _decals.Count >= MaxDecals || !MakeRoom( count ) )
			return null;

		var color = new Vector4( placed.Color.X, placed.Color.Y, placed.Color.Z, 1 );
		var vertices = new DecalShaderVertex[count];
		for ( var i = 0; i < count; i++ )
			vertices[i] = DecalModels.DecalVertex( placed.Triangles[i].Position, placed.Triangles[i].TexCoord, color );

		var live = new LiveDecal
		{
			Placed = placed,
			Material = material,
			Vertices = vertices,
			Layer = (int)placed.Decal.Layer,
			CreatedTick = _decalTicks,
		};
		live.Head = head ?? live;

		if ( head is { Kept: true } )
			_keptDecals++;
		live.Kept = head is null && _decalRandom.Next() < KeepChance;
		if ( live.Kept )
			_keptDecals++;

		_decals.Add( live );
		_decalVertices += count;
		MarkDecalsDirty( live.Layer );

		if ( _keptDecals > MaxKeptDecals && !LetKeptDecalsGo() )
		{
			RemoveDecal( _decals.Count - 1 );
			return null;
		}

		return live;
	}

	// Pushes out the oldest decals that aren't kept until the new one's vertices fit; false if they can't.
	bool MakeRoom( int vertices )
	{
		for ( var i = 0; i < _decals.Count && _decalVertices + vertices > MaxDecalVertices; )
		{
			if ( _decals[i].Kept )
				i++;
			else
				RemoveDecal( i );
		}

		return _decalVertices + vertices <= MaxDecalVertices;
	}

	// Lets kept chains go at random until half the limit are kept; false if a hundred passes don't get there.
	bool LetKeptDecalsGo()
	{
		for ( var pass = 0; pass < MaxLetGoPasses; pass++ )
		{
			foreach ( var decal in _decals )
			{
				if ( _keptDecals <= MaxKeptDecals / 2 )
					return true;
				if ( decal.Head != decal || !decal.Kept || _decalRandom.Next() >= LetGoChance )
					continue;

				decal.Kept = false;
				_keptDecals--;
			}
		}

		return _keptDecals <= MaxKeptDecals / 2;
	}

	void RemoveDecal( int index )
	{
		var decal = _decals[index];
		_decals.RemoveAt( index );
		_decalVertices -= decal.Vertices.Length;
		if ( decal.Kept )
			_keptDecals--;
		MarkDecalsDirty( decal.Layer );
	}

	Material DecalMaterialOf( PlacedDecal placed )
	{
		var key = $"{placed.Decal.Tag.Path}_{placed.BitmapIndex}";
		if ( _decalMaterials.TryGetValue( key, out var material ) )
			return material;

		material = placed.Decal.Bitmap is null ? null : DecalModels.DecalMaterial( $"halo2/effect_decals/{Halo2Mount.ResourcePath( key )}.vmat",
			placed.Decal.Bitmap, placed.BitmapIndex, DecalModels.LayerMode( placed.Decal.Layer ), 1, true, 0 );
		return _decalMaterials[key] = material;
	}

	// A tick of the decals' lives: each lives its life, fading out over its last few seconds; one without a life lasts until pushed out.
	void UpdateDecals()
	{
		_decalTicks++;
		if ( _decalTicks % DecalLevelCheckTicks == 0 )
			DecalLevel();

		for ( var i = _decals.Count - 1; i >= 0; i-- )
		{
			var decal = _decals[i];
			var age = (_decalTicks - decal.CreatedTick) * Tick;
			var (lifetime, decay) = (decal.Placed.Lifetime, decal.Placed.Decay);
			if ( lifetime != 0 && age >= lifetime )
			{
				RemoveDecal( i );
				continue;
			}

			if ( lifetime <= 0 || decay <= 0 || lifetime - age >= decay )
				continue;

			var alpha = (byte)Math.Clamp( (int)(256 * (lifetime - age) / decay), 0, 255 );
			if ( alpha == decal.Alpha )
				continue;

			decal.Alpha = alpha;
			for ( var v = 0; v < decal.Vertices.Length; v++ )
				decal.Vertices[v].Color.w = alpha / 255f;
			MarkDecalsDirty( decal.Layer );
		}
	}

	// How often the decals check the scene still shows their map.
	const int DecalLevelCheckTicks = 15;

	// The collision of the map the scene shows, read once per map; a new map, or none, clears the decals.
	List<DecalGeometry> DecalLevel()
	{
		var map = SceneUpkeep.MapOf( Scene );
		if ( !string.Equals( map, _decalMap, StringComparison.OrdinalIgnoreCase ) )
		{
			ClearDecals();
			_decalMap = map;
			_decalLevel = null;
			_decalLevelLooked = false;
			_decalBitmaps.Clear();
		}

		if ( _decalLevelLooked )
			return _decalLevel;

		_decalLevelLooked = true;
		if ( map is null || Halo2Mount.Current?.GetMap( map ) is not { } cache )
			return null;

		var scenario = new ScenarioTag( cache.Scenario );
		_decalLevel = scenario.StructureBsps.Where( b => b.Bsp is not null ).Select( b => new DecalGeometry( new StructureBspTag( b.Bsp ) ) ).ToList();
		return _decalLevel;
	}

	void ClearDecals()
	{
		_decals.Clear();
		_decalVertices = 0;
		_keptDecals = 0;
		Array.Clear( _recentDecals );
		for ( var layer = 0; layer < DecalLayers; layer++ )
			MarkDecalsDirty( layer );
	}

	// A layer's decals in one vertex buffer, a run per material, newest first; the render thread draws the runs last handed over.
	sealed class DecalLayerDraw
	{
		public SceneCustomObject Object;
		public GpuBuffer<DecalShaderVertex> Buffer;
		public bool Dirty;
		public readonly object Lock = new();
		public List<(Material Material, int Start, int Count)> Runs = new();
	}

	readonly DecalLayerDraw[] _decalLayers = new DecalLayerDraw[DecalLayers];
	readonly DecalShaderVertex[] _decalStaging = new DecalShaderVertex[MaxDecalVertices];

	void MarkDecalsDirty( int layer )
	{
		if ( layer >= 0 && layer < DecalLayers )
			(_decalLayers[layer] ??= new DecalLayerDraw()).Dirty = true;
	}

	// The object a layer of decals draws in, for SceneUpkeep to put in Halo's order; null while it has none.
	public SceneObject DecalLayerObject( int layer ) => _decalLayers[layer]?.Object;

	// Uploads each changed layer's vertices.
	void SnapshotDecals()
	{
		for ( var layer = 0; layer < DecalLayers; layer++ )
		{
			if ( _decalLayers[layer] is not { Dirty: true } draw )
				continue;

			draw.Dirty = false;
			var runs = new List<(Material, int, int)>();
			var count = 0;
			foreach ( var group in _decals.Where( d => d.Layer == layer ).Reverse().GroupBy( d => d.Material ) )
			{
				var start = count;
				foreach ( var decal in group )
				{
					decal.Vertices.CopyTo( _decalStaging, count );
					count += decal.Vertices.Length;
				}
				runs.Add( (group.Key, start, count - start) );
			}

			if ( count > 0 )
			{
				draw.Buffer ??= new GpuBuffer<DecalShaderVertex>( MaxDecalVertices, GpuBuffer.UsageFlags.Vertex, "halo2 effect decals" );
				draw.Buffer.SetData( new ReadOnlySpan<DecalShaderVertex>( _decalStaging, 0, count ) );
			}

			lock ( draw.Lock )
				draw.Runs = runs;

			if ( count > 0 && draw.Object is null )
			{
				draw.Object = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = _ => RenderDecals( draw ) };
				draw.Object.Flags.IsOpaque = false;
				draw.Object.Flags.IsTranslucent = true;
				draw.Object.Flags.CastShadows = false;
			}
		}
	}

	static void RenderDecals( DecalLayerDraw draw )
	{
		List<(Material Material, int Start, int Count)> runs;
		lock ( draw.Lock )
			runs = draw.Runs;

		foreach ( var (material, start, count) in runs )
			Graphics.Draw( draw.Buffer, material, start, count );
	}

	void DeleteDecalLayers()
	{
		for ( var layer = 0; layer < DecalLayers; layer++ )
		{
			_decalLayers[layer]?.Object?.Delete();
			_decalLayers[layer]?.Buffer?.Dispose();
			_decalLayers[layer] = null;
		}
	}
}
