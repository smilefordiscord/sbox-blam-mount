using Blam;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// The cloth of flag widgets (the CTF flag): a grid of the flag tag's cells hanging from the
/// carrying model's markers, waved by halo1_transparent.shader (F_FLAG_CLOTH), with a material
/// for each team.
/// </summary>
/// <remarks>
/// Halo simulates the cloth: every vertex is a point-physics point that gravity pulls down and
/// air friction drags along with the wind (the BSP weather palette's), held one cell from its
/// neighbors, the first column pinned along the markers. That needs state from frame to frame,
/// which a mounted resource can't keep, so the vertex shader poses the cloth from the air
/// moving past it instead: streaming along it, sagging by the speed gravity reaches against the
/// physics' friction, stretched and swung by gusts the way <c>BlamInspect &lt;map&gt; flagsim</c>
/// measures Halo's cloth doing, plus a small ripple running down it. A game passes the air
/// (the level's wind minus the flag's own velocity) per renderer as the HaloFlagWind attribute,
/// and optionally the gusts as HaloFlagGusts (see the shader); without them the cloth streams in
/// a light breeze along its rest direction (the model's -x).
///
/// Built three ways: the carrying model (<c>weapons/flag/flag.vmdl</c>) gets the cloth in a
/// "cloth" body group (choice 0 blue, 1 red, Halo's shader for every team but red being the blue
/// one); <c>&lt;flag tag&gt;_cloth.vmdl</c> is the cloth alone in the same body group, in the carrying
/// model's space; and each team's material is <c>&lt;flag tag&gt;_red.vmat</c> / <c>_blue.vmat</c>.
/// </remarks>
static class FlagCloth
{
	public const string BodyGroup = "cloth";

	/// <summary>The radius Halo gives each cloth vertex as a point-physics point, world units.</summary>
	const float PointRadius = 0.02f;

	/// <summary>Halo's gravity, world units per second squared (0.00357 per tick squared at 30 ticks a second).</summary>
	const float Gravity = 0.0035651792f * 30 * 30;

	const float AirDensity = 0.0011f;
	const float TickSeconds = 1f / 30;

	/// <summary>Breeze (world units per second) the cloth streams in when the game passes no air.</summary>
	const float RestWind = 0.8f;

	/// <summary>Gust strength (world units per second) when the game passes none: Blood Gulch's.</summary>
	const float RestGusts = 0.13f;

	static readonly TagClass FlagClass = new( "flag" );

	public static string ResourceName( string flagTagPath ) => $"{HaloCeMount.ResourcePath( flagTagPath )}_cloth";
	public static string MaterialName( string flagTagPath, bool red ) => $"{HaloCeMount.ResourcePath( flagTagPath )}_{(red ? "red" : "blue")}";

	/// <summary>
	/// Registers each flag tag's cloth model and team materials, the first map with the tag
	/// providing them.
	/// </summary>
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registeredModels, HashSet<string> registeredMaterials )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != FlagClass || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var carrier = FindCarrierModel( cache, tag );
			if ( carrier is null || !new FlagTag( cache, tag ).IsDrawn )
				continue;

			var path = ResourceName( tag.Path );
			if ( registeredModels.Add( path ) )
				context.Add( ResourceType.Model, path, new FlagClothModelLoader( cache.FilePath, tag.Id, carrier.Id ) );

			foreach ( var red in new[] { false, true } )
			{
				var material = MaterialName( tag.Path, red );
				if ( registeredMaterials.Add( material ) )
					context.Add( ResourceType.Material, material, new FlagClothMaterialLoader( cache.FilePath, tag.Id, red ) );
			}
		}
	}

	/// <summary>
	/// The model of the first object carrying the flag as a widget (the flag weapon).
	/// </summary>
	static TagEntry FindCarrierModel( CacheFile cache, TagEntry flagTag ) => cache.Tags
		.Where( t => t.Is( TagClasses.Object ) && ObjectAttachments.ReadWidgets( cache, t ).Any( w => w.Id == flagTag.Id ) )
		.Select( t => ObjectTags.GetObjectModel( cache, t ) )
		.FirstOrDefault( m => m is not null && m.Class == TagClasses.GbxModel );

	/// <summary>
	/// Adds the cloth of every flag that objects using this model carry, in the "cloth" body group
	/// (0 blue, 1 red). Only for models without a skeleton (the flag's has one node): the cloth's
	/// vertices aren't skinned.
	/// </summary>
	public static void AddTo( HaloCeMount host, string name, CacheFile cache, GbxModelTag model, ModelBuilder builder, float scale )
	{
		var flags = cache.Tags
			.Where( t => t.Is( TagClasses.Object ) && ObjectTags.GetObjectModel( cache, t )?.Id == model.Tag.Id )
			.SelectMany( t => ObjectAttachments.ReadWidgets( cache, t ) )
			.Where( w => w.Class == FlagClass )
			.DistinctBy( w => w.Id )
			.ToList();

		foreach ( var tag in flags )
		{
			try
			{
				var flag = new FlagTag( cache, tag );
				if ( !flag.IsDrawn )
					continue;

				var markers = ObjectAttachments.ReadMarkers( cache, model.Tag );
				foreach ( var red in new[] { false, true } )
				{
					var material = CreateMaterial( host, $"{name}/{MaterialName( tag.Path, red )}.vmat", cache, flag, red );
					if ( material is not null )
						GbxModelBuilder.AddMesh( builder, CreateMesh( flag, markers, material, scale ), GbxModelBuilder.AllLods, BodyGroup, red ? 1 : 0 );
				}
			}
			catch ( Exception e )
			{
				host.Logger.Warning( $"{name}: couldn't build the cloth of {tag}: {e.Message}" );
			}
		}
	}

	/// <summary>
	/// The cloth on its own, in the carrying model's space, in the same body group.
	/// </summary>
	public static Model Build( HaloCeMount host, string name, CacheFile cache, TagEntry flagTag, TagEntry modelTag )
	{
		var flag = new FlagTag( cache, flagTag );
		var markers = ObjectAttachments.ReadMarkers( cache, modelTag );
		var builder = Model.Builder.WithName( name );
		foreach ( var red in new[] { false, true } )
		{
			var material = CreateMaterial( host, $"{name}/{MaterialName( flagTag.Path, red )}.vmat", cache, flag, red );
			if ( material is null )
				return null;

			builder.AddMesh( CreateMesh( flag, markers, material, HaloCeMount.WorldScale ), BodyGroup, red ? 1 : 0 );
		}

		return builder.Create();
	}

	/// <summary>
	/// A team's cloth material: its flag shader with the cloth's wave. Only transparent shaders
	/// (every stock flag's) wave; others are drawn as they are, flat.
	/// </summary>
	public static Material CreateMaterial( HaloCeMount host, string name, CacheFile cache, FlagTag flag, bool red )
	{
		var shader = flag.ShaderFor( red );
		if ( shader is null || !HaloMaterials.IsSupported( shader.Class ) )
			return null;

		if ( !ShaderTransparentTag.IsTransparent( shader.Class ) )
		{
			host.Logger.Warning( $"{flag.Tag}: {shader} isn't a transparent shader, its cloth won't wave" );
			return HaloMaterials.Create( host, name, cache, shader, onObject: true );
		}

		var material = TransparentMaterial.Create( host, name, cache, shader );
		if ( material is null )
			return null;

		material.SetFeature( "F_FLAG_CLOTH", 1 );
		material.Set( "g_flFlagSag", SagSpeed( flag.Physics is null ? null : new PointPhysicsTag( cache, flag.Physics ) ) );
		material.Set( "g_flFlagRestWind", RestWind );
		material.Set( "g_flFlagGusts", RestGusts + MathF.Max( flag.WindNoise, 0 ) );
		material.Set( "g_flFlagWorldScale", HaloCeMount.WorldScale );
		return material;
	}

	/// <summary>
	/// How fast (world units per second, downward) a cloth vertex falls through still air once
	/// friction balances gravity, which is how far below the air's direction the cloth streams.
	/// Every tick Halo adds gravity times the physics' air gravity scale, then moves the velocity
	/// a fraction of the way to the wind's: tick length x air friction / (mass x radius), where
	/// the mass per radius cubed is the physics' own plus the air's (density 0.0011).
	/// </summary>
	static float SagSpeed( PointPhysicsTag physics )
	{
		if ( physics is null || physics.NoGravity )
			return 0;

		var massPerDensity = physics.Density > 0 ? physics.MassOverRadiusCubed / physics.Density : 0;
		var mass = (physics.MassOverRadiusCubed + AirDensity * massPerDensity) * PointRadius;
		var pull = mass > 0 ? Math.Clamp( TickSeconds * physics.AirFriction / mass, 0, 1 ) : 1;
		if ( pull <= 0 )
			return 5;

		var fall = Gravity * physics.AirGravityScale * TickSeconds;
		return Math.Clamp( -fall * (1 - pull) / pull, 0, 5 );
	}

	/// <summary>
	/// The cloth's grid at rest (see <see cref="FlagClothGrid"/>). Each vertex carries its distance
	/// from the attached edge (second texture coordinate) and the row's rest direction (tangent)
	/// for the vertex shader to pose it from; the normal is the sheet's, so the shader gets the
	/// edge's direction back as normal x rest. Halo's texture coordinates run across (u) and down
	/// (v) the cloth.
	/// </summary>
	static Mesh CreateMesh( FlagTag flag, IReadOnlyList<ModelMarker> markers, Material material, float scale )
	{
		int width = flag.Width, height = flag.Height;
		var rows = FlagClothGrid.Rows( flag, markers );

		var vertices = new List<HaloVertex>( width * height );
		for ( var x = 0; x < width; x++ )
		{
			for ( var y = 0; y < height; y++ )
			{
				var row = rows[y];
				var rest = (Vector3)row.Rest;
				var distance = x * flag.CellWidth * scale;
				vertices.Add( new HaloVertex
				{
					Position = (Vector3)row.Edge * scale + rest * distance,
					Normal = Vector3.Cross( rest, row.Down ).Normal,
					Tangent = new Vector4( rest, 1 ),
					TexCoord = new Vector2( (float)x / (width - 1), (float)y / (height - 1) ),
					LightmapTexCoord = new Vector2( distance, 0 ),
				} );
			}
		}

		// Two triangles a cell, wound for s&box (the shader draws both sides anyway).
		var indices = new List<int>( (width - 1) * (height - 1) * 6 );
		for ( var x = 0; x < width - 1; x++ )
		{
			for ( var y = 0; y < height - 1; y++ )
			{
				var a = x * height + y;
				var b = (x + 1) * height + y;
				indices.AddRange( [a + 1, b, a, b + 1, b, a + 1] );
			}
		}

		// The cloth can stream any way around its edge, stretched by the wind (up to ~1.5x).
		var reach = ((width - 1) * flag.CellWidth * 1.6f + 0.05f) * scale;
		var bounds = BBox.FromPoints( rows.Select( r => (Vector3)r.Edge * scale ) );
		bounds = new BBox( bounds.Mins - reach, bounds.Maxs + reach );

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );
		mesh.Bounds = bounds;
		return mesh;
	}
}

/// <summary>
/// Loads a flag's cloth on its own (see <see cref="FlagCloth"/>).
/// </summary>
sealed class FlagClothModelLoader( string mapPath, uint flagTagId, uint modelTagId ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var flag = cache.GetTag( flagTagId );
		var model = cache.GetTag( modelTagId );
		return flag is null || model is null ? null : FlagCloth.Build( Host, Path, cache, flag, model );
	}
}

/// <summary>
/// Loads a team's flag cloth material (see <see cref="FlagCloth"/>).
/// </summary>
sealed class FlagClothMaterialLoader( string mapPath, uint flagTagId, bool red ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( flagTagId );
		return tag is null ? null : FlagCloth.CreateMaterial( Host, Path, cache, new FlagTag( cache, tag ), red );
	}
}
