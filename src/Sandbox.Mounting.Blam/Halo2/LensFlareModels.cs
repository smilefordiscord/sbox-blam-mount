using System.Runtime.InteropServices;
using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's lens flares (<see cref="LensFlareTag"/>) as models drawn by halo2_lens_flare.shader: a flare on its own at
/// the origin facing +X (an object's marker), <c>mount://halo2/&lt;lens tag&gt;__lens_flare.vmdl</c>, and a level's
/// suns from its sky's lights (<see cref="AddSuns"/>).
/// </summary>
/// <remarks>
/// As Halo 2 submits them: objects and effects use the
/// upper ends of every reflection's radius and brightness ranges (their input is 1), white light (a light's own flare:
/// the light's colour), and the attachment's
/// object function as the light's value (the renderer's tint, or HaloFunctionScale); the brightness and colour
/// animation functions run on game time. Not done: the rotation animation and rotation functions, the inner occlusion
/// test, the occlusion's smoothing over frames, the sun's glare pass.
/// </remarks>
static class LensFlareModels
{
	public const string ShaderPath = "shaders/halo2_lens_flare.shader";

	public static string ResourceName( string tagPath ) => $"{Halo2Mount.ResourcePath( tagPath )}__lens_flare";

	public static string FullPath( string tagPath ) => $"mount://halo2/{ResourceName( tagPath )}.vmdl";

	// Each built model's flares, in the order of their index (vertex TEXCOORD5.y), for FlareVisibility.
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Model, IReadOnlyList<Flare>> Built = new();

	/// <summary>
	/// A lens flare model's flares (null for any other model).
	/// </summary>
	public static IReadOnlyList<Flare> FlaresOf( Model model ) => model is not null && Built.TryGetValue( model, out var flares ) ? flares : null;

	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Cache != cache || tag.Class != LensFlareTag.Class || tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) )
				continue;

			if ( registered.Add( ResourceName( tag.Path ) ) )
				context.Add( ResourceType.Model, ResourceName( tag.Path ), new LensFlareModelLoader( cache.FilePath, tag.Id ) );
		}

		foreach ( var tag in cache.Tags )
		{
			if ( tag.Cache != cache || tag.Class.ToString() != "ligh" || tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) || LightLens( tag ) is null )
				continue;

			if ( registered.Add( LightResourceName( tag.Path ) ) )
				context.Add( ResourceType.Model, LightResourceName( tag.Path ), new LightLensFlareModelLoader( cache.FilePath, tag.Id ) );
		}
	}

	/// <summary>
	/// A light's own lens flare (ligh +0x90) in the light's colour, <c>mount://halo2/&lt;light tag&gt;__light_lens_flare.vmdl</c>:
	/// Halo 2 submits a dynamic light's flare with the light's colour, and MCC draws
	/// Zanzibar's hinge light flare as the reflection's colour x hinge_spot's (0.64, 0.70, 0.93) to within 1%.
	/// </summary>
	public static string LightResourceName( string lightTagPath ) => $"{Halo2Mount.ResourcePath( lightTagPath )}__light_lens_flare";

	public static string LightFullPath( string lightTagPath ) => $"mount://halo2/{LightResourceName( lightTagPath )}.vmdl";

	/// <summary>
	/// A light's lens flare tag (ligh +0x90), or null.
	/// </summary>
	public static TagEntry LightLens( TagEntry light )
		=> light.Cache.GetTagReference( light.Address + 0x90 ) is { } lens && LensFlareTag.IsLensFlare( lens ) && !string.IsNullOrEmpty( lens.Path ) ? lens : null;

	/// <summary>
	/// A light's colour as objects light it: the upper bound of its diffuse colour (ligh +0x64; the lower bound +0x58 is
	/// where its colour function's input is 0, and an object's input is 1).
	/// </summary>
	public static System.Numerics.Vector3 LightColor( TagEntry light ) => light.Cache.TagData.Vec3( light.Address + 0x64 );

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

	/// <summary>
	/// One flare to build: the lens, where (model space; the direction for a sun) and which way its marker faces, and the
	/// colour of the light it's on (null: white), which tints every reflection that doesn't ignore it.
	/// </summary>
	public sealed record Flare( LensFlareTag Lens, Vector3 Position, Vector3 Forward, bool Sun, System.Numerics.Vector3? Light = null );

	public static Model Build( string name, IEnumerable<Flare> flares )
	{
		var scale = Halo2Mount.WorldScale;
		var meshes = new Dictionary<(string Bitmap, int Index), (List<FlareVertex> Vertices, BBox Bounds)>();
		var list = flares.ToList();
		for ( var f = 0; f < list.Count; f++ )
		{
			var flare = list[f];
			var lens = flare.Lens;
			var bitmap = lens.Bitmap is { Address: not 0 } b && b.Class == BitmapTag.Class ? b : null;
			var bitmapPath = bitmap?.Path ?? @"rasterizer\glow";
			var images = bitmap is null ? 1 : Math.Max( new BitmapTag( bitmap ).Bitmaps.Count, 1 );
			var sun = flare.Sun || lens.IsSun;

			for ( var r = 0; r < lens.Reflections.Count; r++ )
			{
				var reflection = lens.Reflections[r];
				var radius = reflection.Radius.Y;
				var brightness = Math.Clamp( reflection.Brightness.Y, 0, 1 );
				if ( radius <= 0 || brightness <= 0 )
					continue;

				var fixedSize = (reflection.Flags & 2) != 0;
				var flags = (sun ? 1 : 0) + (fixedSize ? 2 : 0) + ((reflection.Flags & 4) != 0 ? 4 : 0) + ((reflection.Flags & 1) != 0 ? 16 : 0)
					+ ((lens.Flags & 2) != 0 ? 32 : 0) + 64 * Math.Clamp( lens.OcclusionOffsetDirection, 0, 2 );
				// "Ignore light colour" (0x10) keeps the reflection's own.
				var light = (reflection.Flags & 0x10) == 0 && flare.Light is { } lightColor ? lightColor : System.Numerics.Vector3.One;
				var corona = r == 0 ? lens.CoronaScale : System.Numerics.Vector2.One;
				if ( corona.X <= 0 ) corona.X = 1;
				if ( corona.Y <= 0 ) corona.Y = 1;

				var vertex = new FlareVertex
				{
					Position = flare.Position,
					Corner = new Vector4( 0, 0, reflection.Position, reflection.RotationOffset.DegreeToRadian() ),
					Sprite = new Vector4( fixedSize ? radius : radius * scale, brightness, flags, lens.OcclusionRadius * scale ),
					Axis = new Vector4( flare.Forward, corona.X ),
					Fade = new Vector4( lens.CosineFalloff, lens.CosineCutoff, lens.NearFadeDistance * scale, lens.FarFadeDistance * scale ),
					Color = new Vector4( reflection.Color.X * light.X, reflection.Color.Y * light.Y, reflection.Color.Z * light.Z, Math.Clamp( 1 - reflection.ModulationFactor, 0, 1 ) ),
					Extra = new Vector4( corona.Y, f, 0, 0 ),
				};

				// Flares are drawn on screen around the flare, so anything that sees it may see them (a constant screen
				// size reaches far); the sun is everywhere.
				var reach = fixedSize ? 4000 : radius * scale * MathF.Max( corona.X, corona.Y ) * 2 + 1;
				var box = sun ? BBox.FromPositionAndSize( 0, 10_000_000 ) : BBox.FromPositionAndSize( flare.Position, reach );
				var key = (bitmapPath, ((reflection.BitmapIndex % images) + images) % images);
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

		var animated = flares.Select( f => f.Lens ).Distinct().Count() == 1 ? Animation( flares.First().Lens ) : default;
		var builder = Model.Builder.WithName( name );
		foreach ( var ((bitmapPath, index), (vertices, bounds)) in meshes )
		{
			var material = Material.Create( $"{name}/{Halo2Mount.ResourcePath( bitmapPath )}_{index}.vmat", ShaderPath );
			if ( material is null )
				continue;

			if ( animated.Texture is not null )
			{
				material.Set( "g_tFlareAnimation", animated.Texture );
				material.Set( "g_vFlareAnimation", new Vector2( animated.Period, animated.Synchronized ? 1 : 0 ) );
			}

			material.Set( "g_tFlare", Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmapPath, index )}.vtex" ) is { IsError: false } map ? map : Texture.White );

			var indices = new List<int>( vertices.Count / 4 * 6 );
			for ( var i = 0; i < vertices.Count; i += 4 )
				indices.AddRange( [i, i + 1, i + 2, i, i + 2, i + 3] );

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = bounds;
			builder.AddMesh( mesh );
		}

		var model = builder.Create();
		if ( model is not null )
			Built.AddOrUpdate( model, list );
		return model;
	}

	/// <summary>
	/// The lens's brightness and colour animation (the first function of each block, of game time in seconds, as
	/// Halo 2 has it): sampled over one period into a 64 x 2 float texture (brightness in row 0, colour in row 1). A periodic
	/// function's period is one over its frequency; the others are taken over a second, repeating (as shader animation
	/// wraps its time input). None without functions.
	/// </summary>
	static (Texture Texture, float Period, bool Synchronized) Animation( LensFlareTag lens )
	{
		var brightness = lens.BrightnessFunctions.FirstOrDefault( f => !f.IsEmpty );
		var color = lens.ColorFunctions.FirstOrDefault( f => !f.IsEmpty );
		if ( brightness is null && color is null )
			return default;

		static float PeriodOf( MappingFunction f ) => f is { Type: MappingFunction.Periodic } && MathF.Abs( f.F( 0x14 ) ) > 1e-4f ? 1 / MathF.Abs( f.F( 0x14 ) ) : 1;
		var period = brightness is not null ? PeriodOf( brightness ) : PeriodOf( color );
		float X( MappingFunction f, float t ) => f.Type == MappingFunction.Periodic ? t : t / period;

		const int samples = 64;
		var data = new float[samples * 2 * 4];
		for ( var i = 0; i < samples; i++ )
		{
			var t = i / (float)samples * period;
			var b = brightness is null ? 1 : brightness.Evaluate( X( brightness, t ) );
			var c = color is null ? System.Numerics.Vector4.One : MappingFunction.ToVector( color.ColorAt( color.Curve( X( color, t ) ) ) );
			data[i * 4] = b;
			data[(samples + i) * 4] = c.X;
			data[(samples + i) * 4 + 1] = c.Y;
			data[(samples + i) * 4 + 2] = c.Z;
			data[(samples + i) * 4 + 3] = 1;
		}

		var texture = Texture.Create( samples, 2, ImageFormat.RGBA32323232F ).WithData( new ReadOnlySpan<float>( data ) ).Finish();
		return (texture, period, (lens.AnimationFlags & 1) != 0);
	}

	/// <summary>
	/// A level's suns: each light of its first sky with a lens flare, toward the light's direction (yaw, pitch at
	/// light +0xC, turned by the sky's global rotation +0x80, as Halo 2 has it), as one model under a "sun flares"
	/// object. Returns the model (built in code: the caller keeps it).
	/// </summary>
	public static Model AddSuns( string name, ScenarioTag scenario )
	{
		var skyTag = scenario.Skies.FirstOrDefault( s => s is not null && s.Class == SkyTag.Class && s.Address != 0 );
		if ( skyTag is null )
			return null;

		var data = skyTag.Cache.TagData;
		var rotation = data.F32( skyTag.Address + 0x80 );
		var flares = new List<Flare>();
		foreach ( var light in data.Elements( skyTag.Address + 0x78, 0x34 ) )
		{
			var lensTag = skyTag.Cache.GetTagReference( light + 0x14 );
			if ( !LensFlareTag.IsLensFlare( lensTag ) )
				continue;

			var yaw = data.F32( light + 0xC ) + rotation;
			var pitch = data.F32( light + 0x10 );
			var direction = new Vector3( MathF.Cos( yaw ) * MathF.Cos( pitch ), MathF.Sin( yaw ) * MathF.Cos( pitch ), MathF.Sin( pitch ) );
			flares.Add( new Flare( new LensFlareTag( lensTag ), direction, -direction, true ) );
		}

		var model = flares.Count == 0 ? null : Build( name, flares );
		if ( model is null )
			return null;

		var go = new GameObject( true, "sun flares" );
		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		return model;
	}

	/// <summary>
	/// The sun the sun glare (<see cref="SunGlare"/>) follows: the first light of the level's first sky whose lens flare
	/// is a sun's (Halo 2 records one, as it draws that flare), toward the light's direction, with the sky fog's
	/// density over the sky (0 without), which fades the glare. Null without one.
	/// </summary>
	public static (Vector3 Direction, float SkyFogDensity)? GlareSun( ScenarioTag scenario )
	{
		var skyTag = scenario.Skies.FirstOrDefault( s => s is not null && s.Class == SkyTag.Class && s.Address != 0 );
		if ( skyTag is null )
			return null;

		var data = skyTag.Cache.TagData;
		var rotation = data.F32( skyTag.Address + 0x80 );
		foreach ( var light in data.Elements( skyTag.Address + 0x78, 0x34 ) )
		{
			var lensTag = skyTag.Cache.GetTagReference( light + 0x14 );
			if ( !LensFlareTag.IsLensFlare( lensTag ) || !new LensFlareTag( lensTag ).IsSun )
				continue;

			var yaw = data.F32( light + 0xC ) + rotation;
			var pitch = data.F32( light + 0x10 );
			var direction = new Vector3( MathF.Cos( yaw ) * MathF.Cos( pitch ), MathF.Sin( yaw ) * MathF.Cos( pitch ), MathF.Sin( pitch ) );
			return (direction, new SkyTag( skyTag ).SkyFog?.MaximumDensity ?? 0);
		}

		return null;
	}
}

sealed class LightLensFlareModelLoader( string mapPath, uint lightTagId ) : Halo2Loader
{
	Model _model;

	protected override object Load()
	{
		if ( _model is not null )
			return _model;

		try
		{
			var light = Host.GetCache( mapPath ).GetTag( lightTagId );
			return _model = LensFlareModels.LightLens( light ) is { } lens
				? LensFlareModels.Build( Path, [new LensFlareModels.Flare( new LensFlareTag( lens ), Vector3.Zero, Vector3.Forward, false, LensFlareModels.LightColor( light ) )] )
				: null;
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"{Path}: {e.Message}" );
			return null;
		}
	}
}

sealed class LensFlareModelLoader( string mapPath, uint tagId ) : Halo2Loader
{
	Model _model;

	protected override object Load()
	{
		if ( _model is not null )
			return _model;

		try
		{
			var tag = Host.GetCache( mapPath ).GetTag( tagId );
			return _model = LensFlareTag.IsLensFlare( tag ) ? LensFlareModels.Build( Path, [new LensFlareModels.Flare( new LensFlareTag( tag ), Vector3.Zero, Vector3.Forward, false )] ) : null;
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"{Path}: {e.Message}" );
			return null;
		}
	}
}
