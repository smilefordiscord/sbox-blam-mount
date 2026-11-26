using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// A Halo 2 level's planar fog: the fog volume below a structure BSP's fog plane (Lockout's abyss), handed to the
/// level's and objects' scene objects as attributes for halo2_opaque.shader (HaloPlanarFog*), which fogs what's
/// below the plane by its depth there through MCC's gradient bitmap.
/// </summary>
/// <remarks>
/// MCC (scratch research notes, planar_fog.md) picks the planar fog of the camera's cluster's fog plane and draws it
/// on geometry and objects in clusters inside that fog (cluster +0x70, <see cref="StructureBspTag.ClusterPlanarFog"/>);
/// above the plane nothing gets any while the camera is above it. Here level geometry in those clusters gets it
/// (halo2_opaque.shader reads the membership from its vertices): Headlong's plane runs at street level with only one
/// cluster under it, and fogging everything below it hazed the streets MCC leaves clear. Objects get none (their
/// cluster isn't tracked; no fog cluster seen so far holds any). The sky never does (its layers have no fog pass),
/// nor do transparent surfaces (MCC only fades their added light by it). The first fog plane whose fog draws is used.
/// </remarks>
sealed class PlanarFog
{
	readonly Vector4 _plane;
	readonly Vector4 _color;
	readonly Vector4 _depths;
	readonly Texture _gradient;

	PlanarFog( Vector4 plane, Vector4 color, Vector4 depths, Texture gradient )
	{
		_plane = plane;
		_color = color;
		_depths = depths;
		_gradient = gradient;
	}

	/// <summary>
	/// The planar fog of a map, or null if it has none that draws.
	/// </summary>
	public static PlanarFog Build( CacheFile cache )
	{
		if ( Find( cache ) is not { } found )
			return null;

		var (fog, normal, distance) = found;

		// rasterizer\gradients image 1: rgb the smoothstep along view depth, alpha the planar fog by view depth (u) and
		// depth below the plane (v).
		var gradient = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( @"rasterizer\gradients", 1 )}.vtex" );
		return new PlanarFog(
			new Vector4( normal.x, normal.y, normal.z, distance ),
			new Vector4( Math.Clamp( fog.Color.X, 0, 1 ), Math.Clamp( fog.Color.Y, 0, 1 ), Math.Clamp( fog.Color.Z, 0, 1 ), Math.Clamp( fog.MaximumDensity, 0, 1 ) ),
			new Vector4( MathF.Max( fog.OpaqueDistance, 1e-4f ), MathF.Max( fog.OpaqueDepth, 1e-4f ), Halo2Mount.WorldScale, (fog.Flags & 0x20) != 0 ? 1 : 0 ),
			gradient );
	}

	/// <summary>
	/// The map's first fog plane whose planar fog draws: the fog and its plane (n·p = d, n up out of the fog).
	/// </summary>
	public static (FogTag Fog, Vector3 Normal, float Distance)? Find( CacheFile cache )
	{
		var scenario = new ScenarioTag( cache.Scenario );
		foreach ( var reference in scenario.StructureBsps )
		{
			if ( reference.Bsp is null )
				continue;

			foreach ( var (palette, normal, distance) in new StructureBspTag( reference.Bsp ).FogPlanes )
			{
				if ( palette < 0 || palette >= scenario.PlanarFogPalette.Count || scenario.PlanarFogPalette[palette] is not { } tag
					|| !tag.Is( FogTag.Class ) || tag.Address == 0 )
					continue;

				var fog = new FogTag( tag );
				if ( (fog.Flags & 0x10) != 0 || fog.MaximumDensity <= 0 )
					continue;

				return (fog, (Vector3)normal, distance);
			}
		}

		return null;
	}

	public void Apply( SceneObject sceneObject )
	{
		var a = sceneObject.Attributes;
		a.Set( "HaloPlanarFogPlane", _plane );
		a.Set( "HaloPlanarFogColor", _color );
		a.Set( "HaloPlanarFogDepths", _depths );
		if ( _gradient is not null )
			a.Set( "HaloPlanarFogGradient", _gradient );
	}
}
