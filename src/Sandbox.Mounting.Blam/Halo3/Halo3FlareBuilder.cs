using System.Runtime.InteropServices;
using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// The level's static lens flares (StaticAttachments: placed scenery's beacons, Sandbox's tower, the effect scenery suns of Edge and Waterfall) as one model drawn by halo2_lens_flare.shader, which builds each reflection on screen around its flare.
// Halo 3's reflections map onto Halo 2's: radius (world units at the flare's depth), brightness, axis offset as the position along the line through the screen centre, rotation offset, x/y scale as the corona scale, colour and modulation; occlusion box = the occlusion offset distance.
// Each frame the shader tests the occlusion box against the depth buffer (Halo 2's frame-to-frame visibility, FlareVisibility, belongs to its own scene upkeep and isn't used). Flares farther than SunDistance (world units from the origin) are drawn as suns: in their direction at the far plane, at their angular size.
static class Halo3FlareBuilder
{
	const string ShaderPath = "shaders/halo2_lens_flare.shader";
	const float SunDistance = 300;

	[StructLayout( LayoutKind.Sequential )]
	struct FlareVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.TexCoord( 0 )] public Vector4 Corner;
		[VertexLayout.TexCoord( 1 )] public Vector4 Sprite;
		[VertexLayout.TexCoord( 2 )] public Vector4 Axis;
		[VertexLayout.TexCoord( 3 )] public Vector4 Fade;
		[VertexLayout.TexCoord( 4 )] public Vector4 Color;
		[VertexLayout.TexCoord( 5 )] public Vector4 Extra;
	}

	// The scenario's static flares in the scene under a "lens flares" object; models keeps the models built in code. Light volumes are drawn by the effect runtime (LevelEffects, Halo3ParticleWorld.LightVolumes).
	public static void Build( Halo3Mount mount, CacheFile cache, List<Model> models )
	{
		StaticAttachments found;
		try
		{
			var scenario = new ScenarioTag( cache.Scenario );
			found = StaticAttachments.Gather( new ScenarioPlan( scenario, ScenarioObjectsBuilder.ShownBspMask( scenario ) ) );
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"Couldn't gather {cache.Name}'s static attachments: {e.Message}" );
			return;
		}

		try
		{
			if ( found.Flares.Count > 0 && Build( $"halo3/{cache.Name}/lens_flares.vmdl", found.Flares ) is { } model )
			{
				models.Add( model );
				var renderer = new GameObject( true, "lens flares" ).AddComponent<ModelRenderer>();
				renderer.Model = model;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"Couldn't build {cache.Name}'s lens flares: {e.Message}" );
		}
	}

	public static Model Build( string name, IReadOnlyList<StaticFlare> flares )
	{
		var scale = Halo3Mount.WorldScale;
		var meshes = new Dictionary<(string Bitmap, int Index), (List<FlareVertex> Vertices, BBox Bounds)>();
		for ( var f = 0; f < flares.Count; f++ )
		{
			var flare = flares[f];
			var lens = flare.Lens;
			var sun = flare.Position.Length() > SunDistance;
			for ( var r = 0; r < lens.Reflections.Count; r++ )
			{
				var reflection = lens.Reflections[r];
				var radius = reflection.Radius;
				var brightness = Math.Clamp( reflection.Brightness, 0, 1 );
				if ( radius <= 0 || brightness <= 0 || (reflection.Flags & 0x100) != 0 )
					continue;

				var bitmap = reflection.BitmapOverride is { Address: not 0 } over && over.Class == BitmapTag.Class ? over : lens.Bitmap is { Address: not 0 } b && b.Class == BitmapTag.Class ? b : null;
				if ( bitmap is null )
					continue;
				var images = Math.Max( 1, BitmapTag.ImagesOf( bitmap ).Count );

				// A sun keeps its angular size: radius over distance at a constant screen size (halo2_lens_flare's fixed size), its occlusion box likewise at Halo 2's 1024 world unit far plane.
				var distance = flare.Position.Length();
				var flags = (sun ? 1 + 2 : 0) + ((reflection.Flags & 4) != 0 ? 4 : 0) + ((reflection.Flags & 1) != 0 ? 16 : 0) + (lens.NoOcclusionTest ? 32 : 0)
					+ 64 * Math.Clamp( lens.OcclusionOffsetDirection, 0, 2 );
				var direction = System.Numerics.Vector3.Normalize( flare.Position );
				var noAngleFade = MathF.Abs( lens.FalloffAngle - lens.CutoffAngle ) < 1e-4f;
				var noDistanceFade = lens.FarFadeDistance <= 0;
				var vertex = new FlareVertex
				{
					Position = sun ? (Vector3)direction : (Vector3)flare.Position * scale,
					Corner = new Vector4( 0, 0, reflection.AxisOffset, reflection.RotationOffset.DegreeToRadian() ),
					Sprite = sun
						? new Vector4( radius / distance, brightness, flags, MathF.Max( lens.OcclusionOffsetDistance, 0.05f ) / distance * 1024 * scale )
						: new Vector4( radius * scale, brightness, flags, MathF.Max( lens.OcclusionOffsetDistance, 0.05f ) * scale ),
					Axis = new Vector4( sun ? -(Vector3)direction : (Vector3)flare.Forward, reflection.ScaleX > 0 ? reflection.ScaleX : 1 ),
					Fade = new Vector4( noAngleFade ? 1 : MathF.Cos( lens.FalloffAngle ), noAngleFade ? 1 : MathF.Cos( lens.CutoffAngle ),
						noDistanceFade ? 0 : lens.NearFadeDistance * scale, noDistanceFade ? 0 : lens.FarFadeDistance * scale ),
					Color = new Vector4( reflection.Color.X, reflection.Color.Y, reflection.Color.Z, Math.Clamp( 1 - reflection.ModulationFactor, 0, 1 ) ),
					Extra = new Vector4( reflection.ScaleY > 0 ? reflection.ScaleY : 1, f, 0, 0 ),
				};

				var reach = radius * scale * MathF.Max( vertex.Axis.w, vertex.Extra.x ) * 2 + 1;
				var box = sun ? BBox.FromPositionAndSize( 0, 10_000_000 ) : BBox.FromPositionAndSize( vertex.Position, reach );
				var key = (bitmap.Path, ((reflection.BitmapIndex % images) + images) % images);
				if ( !meshes.TryGetValue( key, out var mesh ) )
					mesh = (new List<FlareVertex>(), box);
				foreach ( var (x, y) in new[] { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) } )
				{
					vertex.Corner = new Vector4( x, y, vertex.Corner.z, vertex.Corner.w );
					mesh.Vertices.Add( vertex );
				}
				meshes[key] = (mesh.Vertices, mesh.Bounds.AddBBox( box ));
			}
		}

		if ( meshes.Count == 0 )
			return null;

		var builder = Model.Builder.WithName( name );
		foreach ( var ((bitmapPath, index), (vertices, bounds)) in meshes )
		{
			var material = Material.Create( $"{name}/{Halo3Mount.ResourcePath( bitmapPath )}_{index}.vmat", ShaderPath );
			if ( material is null )
				continue;
			material.Set( "g_tFlare", Texture.Load( Halo3Mount.TexturePath( bitmapPath, index ) ) is { IsError: false } map ? map : Texture.White );

			var indices = new List<int>( vertices.Count / 4 * 6 );
			for ( var i = 0; i < vertices.Count; i += 4 )
				indices.AddRange( [i, i + 1, i + 2, i, i + 2, i + 3] );

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = bounds;
			builder.AddMesh( mesh );
		}

		return builder.Create();
	}
}
