using Blam.Halo2;
using MaterialTextures = Sandbox.Mounting.Blam.Halo1.MaterialTextures;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Every placed Halo 2 object's lighting (<see cref="ObjectLight"/>), in one float texture all object
/// materials share; each placed object's renderer tint holds its row index.
/// </summary>
/// <remarks>
/// The same plumbing as the Halo CE mount's table (Halo1/ObjectLightingTable.cs, see its remarks): a mounted
/// scene is serialized after it's built, so per-object values reach the shader only through the renderer
/// tint, which arrives as 8 bits per RGB channel: room for an index, not for the values. Kept separate from
/// CE's so the games' rows and fallbacks don't mix.
///
/// Each object takes <see cref="TexelsPerObject"/> RGBA32F texels: 0 ambient colour, 1 direct colour,
/// 2 direction towards the direct light, 3 indirect colour, 4 direction towards the indirect light (all
/// Halo's gamma space values, the <c>LIGHTMAP_object_*</c> externs); texel 4's alpha is the object's own
/// index, so the shader can check the tint decoded right. Objects whose row isn't found (models a game
/// spawns, the model viewer) get <see cref="ObjectLight.Default"/>.
///
/// Texels 5 to 12 hold the light of objects with PRT data (<see cref="PrtLight"/>, in the object's frame),
/// which their PRT shaders use instead: 5 and 6 the first eight red coefficients, 7 and 8 green's, 9 and 10
/// blue's, 11 the ninth of each, 12 the direction towards the dominant light (world space) and, in its alpha,
/// 1 when the object has a PRT light.
///
/// Texels 13 to 16 hold its change colours, primary to quaternary (<see cref="global::Blam.Halo2.Tags.ObjectTag.InitialChangeColors"/>, gamma
/// space as MCC's <c>OBJECT_change_color_*</c>), alpha 1 where the object has that colour.
/// </remarks>
static class ObjectLightingTable
{
	public const int TexelsPerObject = 17;

	/// <summary>The texel whose alpha is the object's own index.</summary>
	const int IndexTexel = 4;
	public const int ObjectsPerRow = 128;
	const int Width = TexelsPerObject * ObjectsPerRow;

	// Room for 32768 objects: every Halo 2 map's objects together are about 18,000 (1664 x 256 texels, 6.8 MB).
	const int Rows = 256;

	static readonly object _lock = new();
	static readonly List<float> _data = new();
	static readonly Dictionary<string, (int First, int Count)> _maps = new( StringComparer.OrdinalIgnoreCase );
	static readonly List<Material> _materials = new();
	static Texture _texture;

	// Row 0 is never handed out, so a tint that arrives black never finds a row.
	static int _count = 1;

	/// <summary>
	/// Adds a map's objects (or rewrites them, if the map was built before) and returns the index of the
	/// first; the rest follow in order. 0 if the table is full.
	/// </summary>
	/// <param name="prt">Each object's PRT light, null for objects without PRT data (or all of them).</param>
	/// <param name="changeColors">Each object's change colours (null entries: none), or null for none at all.</param>
	public static int Add( string map, IReadOnlyList<ObjectLight> lights, IReadOnlyList<PrtLight> prt = null, IReadOnlyList<System.Numerics.Vector3?[]> changeColors = null )
	{
		lock ( _lock )
		{
			if ( _maps.TryGetValue( map, out var existing ) && existing.Count == lights.Count )
			{
				Write( existing.First, lights, prt, changeColors );
				Rebuild();
				return existing.First;
			}

			if ( _count + lights.Count > Rows * ObjectsPerRow )
				return 0;

			var first = _count;
			_count += lights.Count;
			_data.AddRange( new float[RowsFor( _count ) * Width * 4 - _data.Count] );

			// Row 0's index check (the index texel's alpha) can never match.
			if ( first == 1 )
				_data[IndexTexel * 4 + 3] = -1;

			Write( first, lights, prt, changeColors );
			_maps[map] = (first, lights.Count);
			Rebuild();
			return first;
		}
	}

	/// <summary>
	/// The tint that tells the shader which row is the object's: one byte of the index per channel, each
	/// stored a quarter step over (k + 0.25) / 255 so it survives the scene file's rounding and the
	/// renderer's truncation (see the Halo CE mount's table).
	/// </summary>
	public static Color Tint( int index ) => new( Channel( index ), Channel( index >> 8 ), Channel( index >> 16 ), 1 );

	static float Channel( int value ) => ((value & 0xFF) + 0.25f) / 255f;

	/// <summary>
	/// Gives an object material the table, now and whenever it's rebuilt.
	/// </summary>
	public static void Bind( Material material )
	{
		lock ( _lock )
		{
			if ( !_materials.Contains( material ) )
				_materials.Add( material );
			Set( material );
		}
	}

	static int RowsFor( int count ) => Math.Max( 1, (count + ObjectsPerRow - 1) / ObjectsPerRow );

	static void Write( int first, IReadOnlyList<ObjectLight> lights, IReadOnlyList<PrtLight> prt, IReadOnlyList<System.Numerics.Vector3?[]> changeColors )
	{
		for ( var i = 0; i < lights.Count; i++ )
		{
			var index = first + i;
			var light = lights[i];
			var offset = ((index / ObjectsPerRow) * Width + (index % ObjectsPerRow) * TexelsPerObject) * 4;

			void Texel( int texel, System.Numerics.Vector3 v, float w )
			{
				var o = offset + texel * 4;
				_data[o] = v.X; _data[o + 1] = v.Y; _data[o + 2] = v.Z; _data[o + 3] = w;
			}

			Texel( 0, light.Ambient, 1 );
			Texel( 1, light.Direct, 0 );
			Texel( 2, light.ToDirect, 0 );
			Texel( 3, light.Indirect, 0 );
			Texel( 4, light.ToIndirect, index );

			var p = prt is not null && i < prt.Count ? prt[i] : null;
			WritePrt( offset, p );
			if ( p is not null )
				_prtLights[index] = p;
			else
				_prtLights.Remove( index );
			Texel( 12, p?.Direction ?? default, p is null ? 0 : 1 );

			var colors = changeColors is not null && i < changeColors.Count ? changeColors[i] : null;
			for ( var k = 0; k < 4; k++ )
				Texel( 13 + k, colors?[k] ?? default, colors?[k] is null ? 0 : 1 );
		}
	}

	/// <summary>
	/// Texels 5 to 11 of the object at <paramref name="offset"/> (floats into the data): its PRT light's coefficients.
	/// </summary>
	static void WritePrt( int offset, PrtLight p )
	{
		float C( float[] c, int k ) => p is null ? 0 : c[k];
		for ( var k = 0; k < 2; k++ )
		{
			for ( var channel = 0; channel < 3; channel++ )
			{
				var c = channel == 0 ? p?.Red : channel == 1 ? p?.Green : p?.Blue;
				var o = offset + (5 + channel * 2 + k) * 4;
				for ( var j = 0; j < 4; j++ )
					_data[o + j] = C( c, k * 4 + j );
			}
		}

		var o11 = offset + 11 * 4;
		_data[o11] = C( p?.Red, 8 ); _data[o11 + 1] = C( p?.Green, 8 ); _data[o11 + 2] = C( p?.Blue, 8 ); _data[o11 + 3] = 0;
	}

	/// <summary>Each row's PRT light as written (in its object's frame), for <see cref="TurnPrt"/>.</summary>
	static readonly Dictionary<int, PrtLight> _prtLights = new();

	/// <summary>
	/// The row a renderer tint (<see cref="Tint"/>) points at, 0 for none.
	/// </summary>
	public static int IndexOf( Color tint ) =>
		(int)MathF.Floor( tint.r * 255 + 0.5f ) | (int)MathF.Floor( tint.g * 255 + 0.5f ) << 8 | (int)MathF.Floor( tint.b * 255 + 0.5f ) << 16;

	/// <summary>
	/// Whether a renderer tint points at a row: a placed object's.
	/// </summary>
	public static bool IsRow( Color tint )
	{
		var index = IndexOf( tint );
		lock ( _lock )
			return index >= 1 && index < _count;
	}

	/// <summary>
	/// Turns an object's PRT light into the frame of its moving node (its axes in the object's frame), as MCC turns the
	/// light into each drawn section's node frame every frame: for objects that animate their only node (Zanzibar's
	/// wheel blades and flywheels), whose transfer vectors are in that node's rest frame.
	/// </summary>
	public static void TurnPrt( int index, System.Numerics.Vector3 forward, System.Numerics.Vector3 left, System.Numerics.Vector3 up )
	{
		lock ( _lock )
		{
			if ( _texture is null || index <= 0 || index >= _count || !_prtLights.TryGetValue( index, out var light ) )
				return;

			var offset = ((index / ObjectsPerRow) * Width + (index % ObjectsPerRow) * TexelsPerObject) * 4;
			WritePrt( offset, light.ToLocal( forward, left, up ) );
			var texels = new float[7 * 4];
			for ( var i = 0; i < texels.Length; i++ )
				texels[i] = _data[offset + 5 * 4 + i];
			_texture.Update( new ReadOnlySpan<float>( texels ), (index % ObjectsPerRow) * TexelsPerObject + 5, index / ObjectsPerRow, 7, 1 );
		}
	}

	static void Rebuild()
	{
		_texture ??= Texture.Create( Width, Rows, ImageFormat.RGBA32323232F )
			.WithData( new ReadOnlySpan<float>( new float[Width * Rows * 4] ) )
			.WithDynamicUsage()
			.Finish();
		_texture.Update( new ReadOnlySpan<float>( _data.ToArray() ), 0, 0, Width, RowsFor( _count ) );

		foreach ( var material in _materials )
			Set( material );
	}

	static void Set( Material material )
	{
		var fallback = ObjectLight.Default;
		material.Set( "g_tObjectTable", _texture ?? MaterialTextures.Transparent );
		material.Set( "g_flObjectCount", _texture is null ? 0f : _count );
		material.Set( "g_vFallbackAmbient", (Vector3)fallback.Ambient );
		material.Set( "g_vFallbackDirect", (Vector3)fallback.Direct );
		material.Set( "g_vFallbackToDirect", (Vector3)fallback.ToDirect );
		material.Set( "g_vFallbackIndirect", (Vector3)fallback.Indirect );
		material.Set( "g_vFallbackToIndirect", (Vector3)fallback.ToIndirect );
	}
}
