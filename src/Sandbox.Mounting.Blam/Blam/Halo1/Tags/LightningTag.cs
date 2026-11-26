using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector3/Vector4 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A point a lightning bolt passes through: one of the carrying object's markers.
/// </summary>
/// <param name="NotConnectedToNext">The bolt ends here instead of carrying on to the next marker.</param>
/// <param name="OctavesToNext">How many times the segment to the next marker is subdivided and jittered (each octave halves the pieces).</param>
/// <param name="RandomPositionBounds">How far the point itself wanders around the marker, world units per axis.</param>
/// <param name="Jitter">How far the subdivided points wander off the straight line, world units.</param>
/// <param name="Thickness">Bolt width here, world units.</param>
/// <param name="Tint">RGBA.</param>
public sealed record LightningMarker( string Marker, bool NotConnectedToNext, int OctavesToNext, Vector3 RandomPositionBounds, float Jitter, float Thickness, Vector4 Tint );

/// <summary>
/// Reader for <c>elec</c> (lightning) tags: crackling bolts drawn through a chain of markers on
/// the object that carries them as a widget.
/// </summary>
/// <remarks>
/// Offsets: count 0x2 (bolts drawn at once), near/far fade distance 0x14/0x18, jitter scale,
/// thickness scale, tint modulation and brightness scale sources 0x2C-0x32 (0 none, 1-4 the
/// object's function outputs A-D, or for the tint its change colors A-D), bitmap 0x34, markers block 0x98 (228 bytes: marker name,
/// flags 0x20 (0x1: not connected to next marker), octaves to next marker 0x24, random position
/// bounds 0x74, random jitter 0x80, thickness 0x84, tint 0x88 ARGB), shader block 0xA4
/// (180 bytes: shader flags 0x28, framebuffer blend function 0x2A).
/// </remarks>
public sealed class LightningTag
{
	public int Count { get; }
	public float NearFadeDistance { get; }
	public float FarFadeDistance { get; }
	public int JitterScaleSource { get; }
	public int ThicknessScaleSource { get; }
	public int TintModulationSource { get; }
	public int BrightnessScaleSource { get; }
	public TagEntry Bitmap { get; }
	public IReadOnlyList<LightningMarker> Markers { get; }
	public FramebufferBlendFunction BlendFunction { get; }

	public LightningTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		Count = data.I16( a + 0x2 );
		NearFadeDistance = data.F32( a + 0x14 );
		FarFadeDistance = data.F32( a + 0x18 );
		JitterScaleSource = data.U16( a + 0x2C );
		ThicknessScaleSource = data.U16( a + 0x2E );
		TintModulationSource = data.U16( a + 0x30 );
		BrightnessScaleSource = data.U16( a + 0x32 );
		Bitmap = cache.GetTagReference( data, a + 0x34 );

		var markers = new List<LightningMarker>();
		foreach ( var m in data.Block( a + 0x98, 0xE4 ) )
		{
			markers.Add( new LightningMarker(
				Marker: data.CString( m, 32 ),
				NotConnectedToNext: (data.U16( m + 0x20 ) & 0x1) != 0,
				OctavesToNext: data.I16( m + 0x24 ),
				RandomPositionBounds: data.Vec3( m + 0x74 ),
				Jitter: data.F32( m + 0x80 ),
				Thickness: data.F32( m + 0x84 ),
				Tint: WeatherParticleSystemTag.ReadArgb( data, m + 0x88 ) ) );
		}
		Markers = markers;

		// Without a shader block Halo draws it additively.
		BlendFunction = FramebufferBlendFunction.Add;
		foreach ( var s in data.Block( a + 0xA4, 0xB4 ) )
		{
			BlendFunction = (FramebufferBlendFunction)data.U16( s + 0x2A );
			break;
		}
	}
}
