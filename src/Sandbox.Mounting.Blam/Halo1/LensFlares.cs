using System.Runtime.InteropServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// One lens flare to draw.
/// </summary>
/// <param name="Position">Position (s&amp;box units, in the model's space), or the direction to the sun.</param>
/// <param name="Axis">The way the light faces, or zero for all directions.</param>
/// <param name="Color">The light's color at scale 1, for reflections that take it.</param>
/// <param name="Scale">The light's scale (0-1): Halo interpolates each reflection's radius and brightness by it.</param>
/// <param name="ColorAtZero">The light's color at scale 0 (the light's color goes from this to <paramref name="Color"/> by the scale); null for the same.</param>
sealed record LensFlareInstance( TagEntry LensFlare, Vector3 Position, Vector3 Axis, Vector3 Color, float Scale, bool Sun, Vector3? ColorAtZero = null );

/// <summary>
/// Builds lens flares into models drawn with halo1_lens_flare.shader: a level's, from the sky's
/// lights, the BSP's lens flare markers and the lights on placed objects, in one model; and the
/// flare of a light an object carries, as <c>mount://haloce/&lt;light tag&gt;__lens_flare.vmdl</c>
/// (<see cref="ForLight"/>).
/// </summary>
/// <remarks>
/// Every sprite carries both ends of its radius and brightness ranges and the light's color at
/// both ends of its scale, and the shader interpolates them by the scale, as Halo does each frame
/// for the light's function value. The scale is the sprite's own (a level's flares) times the
/// renderer's tint (red), unless the renderer sets the <c>HaloFunctionScale</c> attribute (0-1),
/// which replaces it. A flare whose light is black at that scale draws nothing.
/// </remarks>
static class LensFlares
{
	public const string ShaderPath = "shaders/halo1_lens_flare.shader";

	public static string ResourceName( string lightTagPath ) => $"{HaloCeMount.ResourcePath( lightTagPath )}__lens_flare";

	public static string FullPath( string lightTagPath ) => $"mount://{EffectPrefabs.Ident}/{ResourceName( lightTagPath )}.vmdl";

	/// <summary>
	/// A light's lens flare on its own, at the model's origin facing +X (the light's marker), for a
	/// renderer that follows the object carrying the light. Its scale is 1, so the renderer's tint
	/// or <c>HaloFunctionScale</c> is the light's function value. Null if the light has no flare
	/// that draws anything.
	/// </summary>
	/// <param name="tint">The object's change color the light is tinted by (see <see cref="ChangeColorTint"/>), white for none.</param>
	public static Model ForLight( HaloCeMount host, string name, CacheFile cache, TagEntry lightTag, System.Numerics.Vector3? tint = null )
	{
		var light = new LightTag( cache, lightTag );
		if ( !ObjectAttachmentPlan.DrawsLensFlare( cache, light.LensFlare ) )
			return null;

		var t = (Vector3)(tint ?? System.Numerics.Vector3.One);
		var flare = new LensFlareInstance( light.LensFlare, Vector3.Zero, Vector3.Forward, (Vector3)light.ColorAt( 1 ) * t, 1, false, (Vector3)light.ColorAt( 0 ) * t );
		return Build( host, name, cache, [flare] );
	}

	[StructLayout( LayoutKind.Sequential )]
	struct FlareVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.TexCoord( 0 )] public Vector4 Corner;
		[VertexLayout.TexCoord( 1 )] public Vector4 Sprite;
		[VertexLayout.TexCoord( 2 )] public Vector4 Axis;
		[VertexLayout.TexCoord( 3 )] public Vector4 Fade;
		[VertexLayout.TexCoord( 4 )] public Vector4 Color;
		[VertexLayout.TexCoord( 5 )] public Vector4 Bounds;
		[VertexLayout.TexCoord( 6 )] public Vector4 Light;
	}

	/// <summary>
	/// The flares of the sky's lights (the sun), toward each light's direction. Halo gives them a
	/// white light at scale 1, whatever color the sky light has.
	/// </summary>
	public static IEnumerable<LensFlareInstance> FromSky( CacheFile cache, TagEntry skyTag )
	{
		var data = cache.TagData;
		foreach ( var light in data.Block( skyTag.Address + 0xC4, 0x74 ) )
		{
			var flare = cache.GetTagReference( data, light );
			if ( flare is null || flare.Class != EffectTagClasses.LensFlare )
				continue;

			// Same layout as SkyTag's lights: direction (yaw, pitch) 0x68.
			var yaw = data.F32( light + 0x68 );
			var pitch = data.F32( light + 0x6C );
			var direction = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
			yield return new LensFlareInstance( flare, direction, Vector3.Zero, Vector3.One, 1, true );
		}
	}

	/// <summary>
	/// The flares the level geometry places on its light fixtures. Halo draws them with a white
	/// light at scale 0, so each sprite takes the lower end of its radius and brightness.
	/// </summary>
	public static IEnumerable<LensFlareInstance> FromBsp( StructureBspEffects bsp ) =>
		bsp.LensFlareMarkers.Select( m => new LensFlareInstance( m.LensFlare, (Vector3)m.Position * HaloCeMount.WorldScale, (Vector3)m.Direction, Vector3.One, 0, false ) );

	/// <summary>
	/// What an object's light attachment is tinted by: Halo multiplies the light's color by the
	/// object's change color the attachment names (1-4: A-D), white for none. Door lock lights take
	/// change color A, which their door's lock state scales from green to red (green at rest).
	/// </summary>
	public static System.Numerics.Vector3 ChangeColorTint( CacheFile cache, ObjectPlacement placement, int changeColor ) =>
		changeColor is >= 1 and <= 4 ? ObjectAppearance.ChangeColors( cache, placement )[changeColor - 1] : System.Numerics.Vector3.One;

	/// <summary>
	/// The flares of lights attached to placed objects, tinted by the objects' change colors.
	/// </summary>
	public static IEnumerable<LensFlareInstance> FromObjects( CacheFile cache, IEnumerable<AttachmentSite> sites )
	{
		var lights = new Dictionary<uint, LightTag>();
		var tints = new Dictionary<(ObjectPlacement, int), Vector3>();
		foreach ( var site in sites )
		{
			var tag = site.Attachment.Type;
			if ( tag.Class != EffectTagClasses.Light )
				continue;

			if ( !lights.TryGetValue( tag.Id, out var light ) )
				lights[tag.Id] = light = new LightTag( cache, tag );

			if ( light.LensFlare is null )
				continue;

			// A light with no function is at 1. Lights driven by an object function (blinking
			// beacons, power) are shown halfway.
			var scale = site.Attachment.PrimaryScale == 0 ? 1f : 0.5f;
			var key = (site.Placement, site.Attachment.ChangeColor);
			if ( !tints.TryGetValue( key, out var tint ) )
				tints[key] = tint = (Vector3)ChangeColorTint( cache, site.Placement, site.Attachment.ChangeColor );
			yield return new LensFlareInstance( light.LensFlare, site.Position, site.Forward, (Vector3)light.ColorAt( 1 ) * tint, scale, false, (Vector3)light.ColorAt( 0 ) * tint );
		}
	}

	public static Model Build( HaloCeMount host, string name, CacheFile cache, IEnumerable<LensFlareInstance> flares )
	{
		var tags = new Dictionary<uint, LensFlareTag>();
		var meshes = new Dictionary<(uint Bitmap, int Index), (TagEntry Bitmap, List<FlareVertex> Vertices)>();
		var bounds = new Dictionary<(uint, int), BBox>();

		foreach ( var flare in flares )
		{
			if ( !tags.TryGetValue( flare.LensFlare.Id, out var lens ) )
				tags[flare.LensFlare.Id] = lens = new LensFlareTag( cache, flare.LensFlare );

			// Some flares (the fluorescent glows on a10's lights) have no bitmap and draw nothing.
			if ( lens.Bitmap is null )
				continue;

			var cosFalloff = lens.CutoffAngle > 0 ? MathF.Cos( lens.FalloffAngle ) : 1;
			var cosCutoff = lens.CutoffAngle > 0 ? MathF.Cos( lens.CutoffAngle ) : 1;
			if ( lens.CutoffAngle <= 0 )
				cosCutoff = cosFalloff = 1;

			// The first reflection's final brightness (its scale function included, which the shader
			// applies) scales every later one, so a flare whose first reflection is dark draws nothing.
			if ( lens.Reflections.Count == 0 )
				continue;
			var first = lens.Reflections[0];
			if ( MathF.Max( first.Brightness.X, first.Brightness.Y ) <= 0 )
				continue;

			// A black light draws no flare; the shader skips the scales where the light is black.
			var colorAtZero = flare.ColorAtZero ?? flare.Color;
			var blackFlags = (colorAtZero == Vector3.Zero ? 128 : 0) + (flare.Color == Vector3.Zero ? 256 : 0);

			for ( var r = 0; r < lens.Reflections.Count; r++ )
			{
				var reflection = lens.Reflections[r];
				if ( MathF.Max( reflection.Radius.X, reflection.Radius.Y ) <= 0 || MathF.Max( reflection.Brightness.X, reflection.Brightness.Y ) <= 0 )
					continue;

				// All zero takes the light's color, tinting (the bitmap's white core stays white).
				var tint = reflection.Tint;
				var untinted = tint == System.Numerics.Vector4.Zero;

				var fixedSize = flare.Sun || reflection.Flags.HasFlag( LensFlareReflectionFlags.RadiusNotScaledByDistance );
				var flags = (flare.Sun ? 1 : 0) + (fixedSize ? 2 : 0) +
					4 * (int)reflection.BrightnessScale + 16 * (r == 0 ? 0 : (int)first.BrightnessScale) + blackFlags +
					(reflection.Flags.HasFlag( LensFlareReflectionFlags.OccludedBySolidObjects ) ? 512 : 0) +
					(reflection.Flags.HasFlag( LensFlareReflectionFlags.RadiusScaledByOcclusion ) ? 1024 : 0) +
					4096 * Math.Clamp( lens.OcclusionOffsetDirection, 0, 2 ) +
					(untinted ? 16384 : 0);

				var color = untinted ? new Vector4( flare.Color, 1 ) : new Vector4( tint.Y, tint.Z, tint.W, tint.X );
				var colorLower = untinted ? colorAtZero : new Vector3( tint.Y, tint.Z, tint.W );

				// The first reflection is the corona, which can be stretched; stored as x, with y = 1/x.
				var stretch = r == 0 ? MathF.Sqrt( lens.CoronaScale.X / lens.CoronaScale.Y ) : 1;
				var corona = r == 0 ? MathF.Sqrt( lens.CoronaScale.X * lens.CoronaScale.Y ) : 1;
				var size = MathF.Max( reflection.Radius.X, reflection.Radius.Y ) * (r == 0 ? MathF.Max( lens.CoronaScale.X, lens.CoronaScale.Y ) : 1);

				// Halo converts a sprite's rotation offset from degrees to radians twice (once more as
				// it draws the sprite), so a 30 degree offset turns it half a degree.
				var rotation = reflection.RotationOffset.DegreeToRadian().DegreeToRadian();

				var sprite = new Vector4( reflection.Radius.X * corona * HaloCeMount.WorldScale, reflection.Brightness.X, flags, lens.OcclusionRadius * HaloCeMount.WorldScale );
				var fade = new Vector4( cosFalloff, cosCutoff, lens.NearFadeDistance * HaloCeMount.WorldScale, lens.FarFadeDistance * HaloCeMount.WorldScale );
				AddSprite( meshes, bounds, (lens.Bitmap.Id, reflection.BitmapIndex), lens.Bitmap, flare, size, new FlareVertex
				{
					Position = flare.Position,
					Corner = new Vector4( 0, 0, reflection.Position, rotation ),
					Sprite = sprite,
					Axis = new Vector4( flare.Axis, stretch ),
					Fade = fade,
					Color = color,
					Bounds = new Vector4( reflection.Radius.Y * corona * HaloCeMount.WorldScale, reflection.Brightness.Y, r == 0 ? 1 : first.Brightness.X, r == 0 ? 1 : first.Brightness.Y ),
					Light = new Vector4( colorLower, flare.Scale ),
				} );
			}

			// Halo's sun glow (see halo1_lens_flare.shader), for suns: the sun flag, or the occlusion
			// radius every sky's sun has. It reaches about 0.45 of the screen height from the sun.
			if ( lens.IsSun || lens.OcclusionRadius == 50 )
			{
				AddSprite( meshes, bounds, (lens.Bitmap.Id, -1), lens.Bitmap, flare, 1, new FlareVertex
				{
					Position = flare.Position,
					Sprite = new Vector4( 0, 1, (flare.Sun ? 1 : 0) + 2 + 64, lens.OcclusionRadius * HaloCeMount.WorldScale ),
					Axis = new Vector4( flare.Axis, 1 ),
					Fade = new Vector4( 1, 1, 0, 0 ),
					Color = new Vector4( 1, 1, 1, -1 ),
					Bounds = new Vector4( 0, 1, 1, 1 ),
					Light = new Vector4( 1, 1, 1, 1 ),
				} );
			}
		}

		if ( meshes.Count == 0 )
			return null;

		var builder = Model.Builder.WithName( name );
		foreach ( var (key, (bitmap, vertices)) in meshes )
		{
			var material = Material.Create( $"{name}/{bitmap.Path.Replace( '\\', '/' )}_{key.Index}.vmat", ShaderPath );
			if ( material is null )
				continue;

			// A reflection's bitmap index wraps around the bitmap's images (the Scorpion's headlight asks
			// for image 2 of one).
			material.Set( "g_tFlare", MaterialTextures.LoadPermutation( host, material, cache, bitmap, key.Index ) ?? MaterialTextures.White );

			var indices = new List<int>( vertices.Count / 4 * 6 );
			for ( var i = 0; i < vertices.Count; i += 4 )
				indices.AddRange( [i, i + 1, i + 2, i, i + 2, i + 3] );

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = bounds[key];
			builder.AddMesh( mesh );
		}

		return builder.Create();
	}

	/// <summary>
	/// Adds a sprite to the mesh for its bitmap: <paramref name="vertex"/> at each of its four corners.
	/// </summary>
	/// <param name="size">The sprite's radius in world units, for its bounds.</param>
	static void AddSprite( Dictionary<(uint, int), (TagEntry Bitmap, List<FlareVertex> Vertices)> meshes, Dictionary<(uint, int), BBox> bounds,
		(uint, int) key, TagEntry bitmap, LensFlareInstance flare, float size, FlareVertex vertex )
	{
		if ( !meshes.TryGetValue( key, out var mesh ) )
			meshes[key] = mesh = (bitmap, new List<FlareVertex>());

		foreach ( var (x, y) in new[] { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) } )
		{
			vertex.Corner = new Vector4( x, y, vertex.Corner.z, vertex.Corner.w );
			mesh.Vertices.Add( vertex );
		}

		// Flares are drawn on screen around the light, so anything that sees the light
		// may see them. The sun is everywhere.
		var box = flare.Sun ? BBox.FromPositionAndSize( 0, 10_000_000 ) : BBox.FromPositionAndSize( flare.Position, size * HaloCeMount.WorldScale * 2 + 1 );
		bounds[key] = bounds.TryGetValue( key, out var b ) ? b.AddBBox( box ) : box;
	}
}
