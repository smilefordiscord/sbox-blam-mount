using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector4 when this compiles into the mount.
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// One frame of a light volume: where its sprites run along the marker's forward and how big
/// and bright they are from the first (hither) to the last (yon).
/// </summary>
/// <param name="OffsetFromMarker">World units along the marker's forward to the first sprite.</param>
/// <param name="OffsetExponent">Bends the spacing: sprite i sits at (i / (count - 1))^exponent of the length.</param>
/// <param name="Length">World units from the first sprite to the last.</param>
/// <param name="RadiusHither">World units: half the width of the first sprite's square.</param>
/// <param name="ColorHither">RGBA in gamma space; alpha is the brightness.</param>
public sealed record LightVolumeFrame( float OffsetFromMarker, float OffsetExponent, float Length, float RadiusHither, float RadiusYon, float RadiusExponent, Vector4 ColorHither, Vector4 ColorYon, float ColorExponent, float BrightnessExponent );

/// <summary>
/// Reader for <c>mgs2</c> (light volume) tags: the glowing bolts of plasma weapons and the glows of
/// rockets and needles, carried as object widgets. A light volume is a row of camera facing
/// sprites strung along a marker's forward, added to the frame.
/// </summary>
/// <remarks>
/// Offsets: attachment marker 0x0, flags 0x22 (0x1 interpolate color in HSV, 0x2 along the
/// farthest hue path), near/far fade distance 0x34/0x38 (along the camera's forward), perpendicular
/// and parallel brightness scale 0x3C/0x40, brightness scale source 0x44 (0 none, 1-4 A-D out),
/// map 0x5C, sequence index 0x6C, count 0x6E (sprites), frame animation source 0xB8, frames
/// 0x120 (176 bytes: offset from marker 0x10, offset exponent 0x14, length 0x18, radius hither/yon
/// 0x3C/0x40, radius exponent 0x44, color hither/yon 0x68/0x78 ARGB, color exponent 0x88,
/// brightness exponent 0x8C). Halo only ever draws the first frame (it interpolates the first
/// frame with itself).
/// </remarks>
public sealed class LightVolumeTag
{
	public static readonly TagClass Class = new( "mgs2" );

	public string Marker { get; }
	public ushort Flags { get; }
	public float NearFadeDistance { get; }
	public float FarFadeDistance { get; }
	public float PerpendicularBrightnessScale { get; }
	public float ParallelBrightnessScale { get; }
	public int BrightnessScaleSource { get; }
	public TagEntry Map { get; }
	public int SequenceIndex { get; }
	public int Count { get; }
	public IReadOnlyList<LightVolumeFrame> Frames { get; }

	public LightVolumeTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		Marker = data.CString( a, 32 );
		Flags = data.U16( a + 0x22 );
		NearFadeDistance = data.F32( a + 0x34 );
		FarFadeDistance = data.F32( a + 0x38 );
		PerpendicularBrightnessScale = data.F32( a + 0x3C );
		ParallelBrightnessScale = data.F32( a + 0x40 );
		BrightnessScaleSource = data.U16( a + 0x44 );
		Map = cache.GetTagReference( data, a + 0x5C );
		SequenceIndex = data.I16( a + 0x6C );
		Count = data.I16( a + 0x6E );

		var frames = new List<LightVolumeFrame>();
		foreach ( var f in data.Block( a + 0x120, 0xB0 ) )
		{
			frames.Add( new LightVolumeFrame(
				OffsetFromMarker: data.F32( f + 0x10 ),
				OffsetExponent: data.F32( f + 0x14 ),
				Length: data.F32( f + 0x18 ),
				RadiusHither: data.F32( f + 0x3C ),
				RadiusYon: data.F32( f + 0x40 ),
				RadiusExponent: data.F32( f + 0x44 ),
				ColorHither: WeatherParticleSystemTag.ReadArgb( data, f + 0x68 ),
				ColorYon: WeatherParticleSystemTag.ReadArgb( data, f + 0x78 ),
				ColorExponent: data.F32( f + 0x88 ),
				BrightnessExponent: data.F32( f + 0x8C ) ) );
		}
		Frames = frames;
	}

	/// <summary>
	/// Halo's sprites, first to last: each one's distance along the marker's forward and radius
	/// (world units), and color (RGB in gamma space, A the brightness before the view dependent
	/// scales). Colors are interpolated in RGB; the HSV flags aren't applied.
	/// </summary>
	public IReadOnlyList<(float Offset, float Radius, Vector4 Color)> Sprites()
	{
		var sprites = new List<(float, float, Vector4)>();
		if ( Count <= 0 || Frames.Count == 0 )
			return sprites;

		var frame = Frames[0];
		if ( (frame.ColorHither.W <= 0 && frame.ColorYon.W <= 0) || (frame.RadiusHither <= 0 && frame.RadiusYon <= 0) )
			return sprites;

		static float Pow( float value, float exponent ) => exponent != 1 ? MathF.Pow( value, exponent ) : value;

		for ( var i = 0; i < Count; i++ )
		{
			// One sprite divides by zero in Halo; it sits at the marker end.
			var t = Count > 1 ? (float)i / (Count - 1) : 0;
			var offset = Pow( t, frame.OffsetExponent );
			var radius = float.Lerp( frame.RadiusHither, frame.RadiusYon, Pow( offset, frame.RadiusExponent ) );
			var c = Pow( offset, frame.ColorExponent );
			var b = Pow( offset, frame.BrightnessExponent );
			var color = new Vector4(
				float.Lerp( frame.ColorHither.X, frame.ColorYon.X, c ),
				float.Lerp( frame.ColorHither.Y, frame.ColorYon.Y, c ),
				float.Lerp( frame.ColorHither.Z, frame.ColorYon.Z, c ),
				float.Lerp( frame.ColorHither.W, frame.ColorYon.W, b ) );
			if ( radius > 0 )
				sprites.Add( (offset * frame.Length + frame.OffsetFromMarker, radius, color) );
		}

		return sprites;
	}
}
