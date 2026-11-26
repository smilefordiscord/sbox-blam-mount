using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds the lightning that placed objects carry as widgets (the arcs along b40's bridges, c10's
/// lab equipment, c20's index platform) into one model of bolt ribbons, which
/// halo1_particles.shader redraws with fresh jitter 30 times a second.
/// </summary>
/// <remarks>
/// Each segment between two markers is a strip of 2^octaves + 1 points, two vertices each. Every
/// vertex carries both markers, the jitter at each end and its point's index, so the vertex
/// shader can redo Halo's midpoint displacement on its own (see <see cref="LightningChains"/>).
/// The object functions that scale a bolt's jitter, thickness and brightness, and the change
/// color tinting it, are passed as periodic waves; functions driven by the object's inputs play
/// as if those inputs were 1. Marker random bounds are applied along the world axes, as the box
/// around the marker's rotated bounds.
/// </remarks>
static class LightningWidgets
{
	sealed class Batch
	{
		public Material Material;
		public readonly List<ParticleVertex> Vertices = new();
		public readonly List<int> Indices = new();
		public BBox? Bounds;
	}

	public static Model Build( HaloCeMount host, string name, CacheFile cache, ScenarioTag scenario )
	{
		var batches = new Dictionary<(uint, uint), Batch>();
		var widgetCache = new Dictionary<uint, List<TagEntry>>();
		var markerCache = new Dictionary<uint, IReadOnlyList<ModelMarker>>();
		var tags = new Dictionary<uint, (LightningTag Tag, List<LightningChain> Chains)>();

		// Every bolt in the level gets its own random numbers, so no two flicker in step.
		var bolts = 0;

		foreach ( var placement in scenario.Objects )
		{
			if ( placement.NotAutomatically || !ObjectAttachmentSites.StaticKinds.Contains( placement.Kind ) )
				continue;

			if ( !widgetCache.TryGetValue( placement.Object.Id, out var widgets ) )
			{
				widgets = ObjectAttachments.ReadWidgets( cache, placement.Object ).Where( w => w.Class == EffectTagClasses.Lightning ).ToList();
				widgetCache[placement.Object.Id] = widgets;
			}

			if ( widgets.Count == 0 )
				continue;

			var modelTag = ObjectTags.GetObjectModel( cache, placement.Object );
			var modelId = modelTag?.Id ?? 0xFFFFFFFF;
			if ( !markerCache.TryGetValue( modelId, out var markers ) )
				markerCache[modelId] = markers = ObjectAttachments.ReadMarkers( cache, modelTag );

			var (origin, rotation) = ObjectAttachmentSites.PlacementFrame( placement );

			foreach ( var widget in widgets )
			{
				if ( !tags.TryGetValue( widget.Id, out var lightning ) )
				{
					try
					{
						var tag = new LightningTag( cache, widget );
						lightning = (tag, LightningChains.FromTag( tag ));
					}
					catch ( Exception e )
					{
						host.Logger.Warning( $"{widget}: couldn't read lightning: {e.Message}" );
						lightning = (null, new List<LightningChain>());
					}

					tags[widget.Id] = lightning;
				}

				if ( lightning.Tag?.Bitmap is null || lightning.Chains.Count == 0 )
					continue;

				// Where each of the tag's markers is in the world. Halo draws nothing without the first.
				var frames = lightning.Tag.Markers
					.Select( m => ObjectAttachmentSites.FirstMarker( markers, m.Marker ) is { } marker ? ObjectAttachmentSites.MarkerFrame( marker, origin, rotation ) : ((Vector3, Rotation)?)null )
					.ToList();
				if ( frames[0] is null )
					continue;

				var key = (widget.Id, placement.Object.Id);
				if ( !batches.TryGetValue( key, out var batch ) )
				{
					var material = CreateMaterial( host, $"{name}/{batches.Count}_{System.IO.Path.GetFileName( widget.Path )}.vmat", cache, lightning.Tag, placement );
					if ( material is null )
						continue;

					batches[key] = batch = new Batch { Material = material };
				}

				for ( var instance = 0; instance < lightning.Tag.Count; instance++ )
				{
					foreach ( var chain in lightning.Chains )
					{
						if ( chain.Segments.All( s => frames[s.Start] is not null && frames[s.End] is not null ) )
							AddBolt( batch, lightning.Tag, chain, frames, bolts++ );
					}
				}
			}
		}

		if ( batches.Count == 0 )
			return null;

		var builder = Model.Builder.WithName( name );
		foreach ( var batch in batches.Values.Where( b => b.Indices.Count > 0 ) )
			builder.AddMesh( ParticleMaterial.CreateMesh( batch.Material, batch.Vertices, batch.Indices, batch.Bounds.Value ) );

		return builder.Create();
	}

	static Material CreateMaterial( HaloCeMount host, string name, CacheFile cache, LightningTag tag, ObjectPlacement placement )
	{
		var material = ParticleMaterial.Create( host, name, cache, tag.Bitmap, 0, tag.BlendFunction, ParticleRenderMode.ScreenFacing, weather: false );
		if ( material is null )
			return null;

		material.Set( "g_flLightning", 1f );

		var functions = ObjectFunctions.Read( cache, placement.Object );
		SetFunction( material, "Jitter", functions, tag.JitterScaleSource );
		SetFunction( material, "Thickness", functions, tag.ThicknessScaleSource );
		SetFunction( material, "Brightness", functions, tag.BrightnessScaleSource );

		// The tint is one of the object's change colors. One scaled by a function output moves
		// between its bounds with it; others are fixed when the object is made.
		var colors = ObjectChangeColors.Read( cache, placement.Object );
		var index = tag.TintModulationSource - 1;
		var lower = System.Numerics.Vector3.One;
		var upper = System.Numerics.Vector3.One;
		var tintFunction = 0;
		if ( index >= 0 && index < colors.Count )
		{
			var color = colors[index];
			if ( color.ScaleBy is >= 5 and <= 8 )
			{
				lower = color.Lower;
				upper = color.Upper;
				tintFunction = color.ScaleBy - 4;
			}
			else
			{
				var random = new Random( HashCode.Combine( placement.Position.X, placement.Position.Y, placement.Position.Z ) );
				lower = upper = ObjectChangeColors.Pick( color, () => random.NextSingle() );
			}
		}

		material.Set( "g_vLightningTintLower", (Vector3)lower );
		material.Set( "g_vLightningTintUpper", (Vector3)upper );
		SetFunction( material, "Tint", functions, tintFunction );
		return material;
	}

	/// <summary>
	/// Sets one of the material's object functions from function output <paramref name="output"/>
	/// (1-4 A-D out). None is a constant 1; an output the object doesn't have is 0.
	/// </summary>
	static void SetFunction( Material material, string source, IReadOnlyList<ObjectFunction> functions, int output )
	{
		var wave = new Vector4( 0, 1, 0, 1 );
		var range = new Vector2( 0, 1 );

		if ( output >= 1 && output <= 4 )
		{
			if ( output <= functions.Count )
			{
				var f = functions[output - 1];
				var (scale, offset, min, max) = f.Mapping;
				wave = new Vector4( (float)f.Wave, f.Period, scale, offset );
				range = new Vector2( min, max );
			}
			else
			{
				wave = new Vector4( 0, 1, 0, 0 );
				range = new Vector2( 0, 0 );
			}
		}

		material.Set( $"g_vLightning{source}", wave );
		material.Set( $"g_vLightning{source}Range", range );
	}

	static void AddBolt( Batch batch, LightningTag tag, LightningChain chain, List<(Vector3 Position, Rotation Rotation)?> frames, int bolt )
	{
		var scale = HaloCeMount.WorldScale;
		for ( var s = 0; s < chain.Segments.Count; s++ )
		{
			var segment = chain.Segments[s];
			var start = tag.Markers[segment.Start];
			var end = tag.Markers[segment.End];
			var a = frames[segment.Start].Value;
			var b = frames[segment.End].Value;
			var (wanderA, packedA) = PackBounds( start.RandomPositionBounds, a.Rotation );
			var (wanderB, packedB) = PackBounds( end.RandomPositionBounds, b.Rotation );

			var n = segment.PointCount;
			var first = batch.Vertices.Count;
			for ( var k = 0; k <= n; k++ )
			{
				// Halo averages width and color into each midpoint, which is linear along the segment.
				var t = (float)k / n;
				var color = System.Numerics.Vector4.Lerp( start.Tint, end.Tint, t );
				var width = float.Lerp( start.Thickness, end.Thickness, t ) * scale;
				var u = (float)(segment.FirstPoint + k) / chain.PointCount;

				for ( var side = 1; side >= -1; side -= 2 )
				{
					batch.Vertices.Add( new ParticleVertex
					{
						Position = a.Position,
						Corner = new Vector4( b.Position.x, b.Position.y, b.Position.z, start.Jitter * scale ),
						Color = new Vector4( color.X, color.Y, color.Z, color.W ),
						Particle = new Vector4( k, side * (segment.Octaves + 1), width, u ),
						Motion = new Vector4( end.Jitter * scale, bolt * 64 + s, packedA, packedB ),
					} );
				}
			}

			for ( var k = 0; k < n; k++ )
			{
				var v = first + k * 2;
				batch.Indices.AddRange( [v, v + 1, v + 3, v, v + 3, v + 2] );
			}

			// Jitter adds up to less than twice the largest over the octaves.
			var margin = 2 * MathF.Max( start.Jitter, end.Jitter ) * scale + MathF.Max( start.Thickness, end.Thickness ) * scale + 1;
			var box = BBox.FromPositionAndSize( a.Position, (wanderA + margin) * 2 ).AddBBox( BBox.FromPositionAndSize( b.Position, (wanderB + margin) * 2 ) );
			batch.Bounds = batch.Bounds is { } bounds ? bounds.AddBBox( box ) : box;
		}
	}

	/// <summary>
	/// A marker's random position bounds (world units, in the marker's space) as the box around
	/// them in the world, in s&amp;box units, packed for the shader (8 bits per axis in steps of
	/// 0.5 units), and how far that lets the marker wander.
	/// </summary>
	static (float Reach, float Packed) PackBounds( System.Numerics.Vector3 bounds, Rotation rotation )
	{
		var scale = HaloCeMount.WorldScale;
		var x = rotation * new Vector3( bounds.X * scale, 0, 0 );
		var y = rotation * new Vector3( 0, bounds.Y * scale, 0 );
		var z = rotation * new Vector3( 0, 0, bounds.Z * scale );
		var extents = new Vector3(
			MathF.Abs( x.x ) + MathF.Abs( y.x ) + MathF.Abs( z.x ),
			MathF.Abs( x.y ) + MathF.Abs( y.y ) + MathF.Abs( z.y ),
			MathF.Abs( x.z ) + MathF.Abs( y.z ) + MathF.Abs( z.z ) );

		int Quantize( float value ) => Math.Clamp( (int)MathF.Round( value * 2 ), 0, 255 );
		var packed = Quantize( extents.x ) | Quantize( extents.y ) << 8 | Quantize( extents.z ) << 16;
		return (extents.Length, packed);
	}
}
