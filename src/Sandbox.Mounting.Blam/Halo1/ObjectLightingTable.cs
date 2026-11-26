using Blam.Halo1;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Per-object shading data (lighting and change colors) for every placed object, in one float
/// texture all model materials share. Each placed object's renderer tint holds its index.
/// </summary>
/// <remarks>
/// A mounted scene is serialized after it's built, so per-object values can only reach the shader
/// through properties the scene keeps. The renderer tint is the only one, and it arrives as 8 bits
/// per RGB channel (the scene renderer packs it with <c>PackRgb888</c>; alpha below 1 makes the
/// object fade): enough for a 24-bit index, not for the values themselves.
///
/// Objects whose row isn't found (models a game spawns, or if the tint ever arrives garbled) are
/// lit by <see cref="MovingObjectLights"/>, the way Halo lights a moving object, through their
/// renderer's attributes; until it has, from the last built map's light volume
/// (<see cref="ObjectLightVolume"/>) at their position, and where that has nothing, with the
/// map's average light; never by the scene's sun.
///
/// Every object takes <see cref="TexelsPerObject"/> RGBA32F texels in a row:
/// 0 ambient light, 1 main light color, 2 direction toward it, 3 reflection tint, 4 bounce light
/// color, 5 direction toward it (all in Halo's gamma space units), 6-9 change colors A-D. Texel 9's
/// alpha is the object's own index, so
/// the shader can check it decoded the tint correctly. Each built map appends its objects (a map
/// built again reuses its rows) to one fixed size texture that's updated in place, so a material
/// bound once always sees every map's rows. Materials are held strongly: with weak references,
/// the managed wrappers were collected while the materials stayed in use, and they kept a stale
/// object count and fallback light.
/// </remarks>
static class ObjectLightingTable
{
	public const int TexelsPerObject = 10;
	public const int ObjectsPerRow = 128;
	const int Width = TexelsPerObject * ObjectsPerRow;

	// Room for 32768 objects, more than every Halo CE map together.
	const int Rows = 256;

	static readonly object _lock = new();
	static readonly List<float> _data = new();

	// Each row's lighting and where it was worked out (Halo world units), by index, for MovingObjectLights.
	static readonly List<(ObjectLight Light, System.Numerics.Vector3 Center)> _rows = new() { default };
	static readonly Dictionary<string, (int First, int Count)> _maps = new( StringComparer.OrdinalIgnoreCase );
	static readonly List<Material> _materials = new();
	static Texture _texture;

	// Row 0 is never handed out, so a tint that arrives black never finds a row.
	static int _count = 1;

	static int _version;

	// The last built map's light volume: four atlas images, the grid's corner and cell size
	// (s&box units), and (cells across, cells deep, slices, slices per atlas row).
	static Texture[] _volume;
	static Vector3 _volumeMin;
	static Vector3 _volumeCell;
	static Vector4 _volumeSize;

	// The average light of the active map: what objects get when their row can't be found.
	static ObjectLight _fallback = new( new System.Numerics.Vector3( 0.5f ), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ, new System.Numerics.Vector3( 0.5f ) );

	// Each built map's light volume and average light, by map name, and the map whose are bound to
	// the materials (see Activate): a game can go back to a map whose scene was built before.
	static readonly Dictionary<string, (Texture[] Volume, Vector3 Min, Vector3 Cell, Vector4 Size)> _volumes = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, ObjectLight> _fallbacks = new( StringComparer.OrdinalIgnoreCase );
	static string _active;


	/// <summary>
	/// One object's row of the table.
	/// </summary>
	// Center: where the lighting was worked out, the placed object's bounding sphere center in Halo world units.
	public readonly record struct Entry( ObjectLight Light, System.Numerics.Vector3[] ChangeColors, System.Numerics.Vector3 Center );

	/// <summary>
	/// Adds a map's objects (or finds them, if the map was built before) and returns the index of
	/// the first. The rest follow in order.
	/// </summary>
	public static int Add( string map, IReadOnlyList<Entry> entries )
	{
		lock ( _lock )
		{
			_fallback = Average( entries, _fallback );

			if ( _maps.TryGetValue( map, out var existing ) && existing.Count == entries.Count )
			{
				Write( existing.First, entries );
				Rebuild();
				_version++;
				return existing.First;
			}

			// Full: row 0 is never found, so the map's objects get the average light.
			if ( _count + entries.Count > Rows * ObjectsPerRow )
				return 0;

			var first = _count;
			_count += entries.Count;
			_data.AddRange( new float[RowsFor( _count ) * Width * 4 - _data.Count] );
			_rows.AddRange( new (ObjectLight, System.Numerics.Vector3)[_count - _rows.Count] );

			// Row 0's index check (the last texel's alpha) can never match.
			if ( first == 1 )
				_data[(TexelsPerObject - 1) * 4 + 3] = -1;
			Write( first, entries );
			_maps[map] = (first, entries.Count);
			Rebuild();
			_version++;
			return first;
		}
	}

	/// <summary>
	/// The tint that tells the model shader which row is the object's: one byte of the index per
	/// channel.
	/// </summary>
	/// <remarks>
	/// Each byte k is stored as (k + 0.25) / 255, not k / 255. The scene is serialized after it's
	/// built, which writes the tint with 5 decimals (1/255 becomes 0.00392), and the renderer packs
	/// it to 8 bits by truncating: 0.00392 x 255 = 0.9996 became 0. That moved 125 of the 256 byte
	/// values down by one, so objects read a neighbour's row or none (b30's and Deathisland's
	/// wrongly lit rocks, Blood Gulch's tank). A quarter step over stays k when truncated or rounded.
	/// </remarks>
	public static Color Tint( int index ) => new( Channel( index ), Channel( index >> 8 ), Channel( index >> 16 ), 1 );

	static float Channel( int value ) => ((value & 0xFF) + 0.25f) / 255f;

	/// <summary>
	/// Whether the model shader finds a row for a renderer with this tint: decoded the way the
	/// shader decodes it (one byte per channel, rounded), an index the table has handed out.
	/// </summary>
	public static bool IsRow( Color tint ) => TryGetRow( tint, out _, out _ );

	/// <summary>Changes whenever rows are added or written, so what <see cref="TryGetRow"/> found can be kept until then.</summary>
	public static int Version => System.Threading.Volatile.Read( ref _version );

	// The lighting in the row a renderer's tint names and where it was worked out (Halo world units), if it names one.
	public static bool TryGetRow( Color tint, out ObjectLight light, out System.Numerics.Vector3 center )
	{
		static int Byte( float c ) => (int)MathF.Round( Math.Clamp( c, 0, 1 ) * 255 );
		var index = Byte( tint.r ) + Byte( tint.g ) * 256 + Byte( tint.b ) * 65536;
		lock ( _lock )
		{
			if ( index >= 1 && index < _count && _texture is not null )
			{
				(light, center) = _rows[index];
				return true;
			}
		}

		light = default;
		center = default;
		return false;
	}

	/// <summary>
	/// The last built map's average object light: what objects get when nothing else lights them.
	/// </summary>
	public static ObjectLight Fallback
	{
		get
		{
			lock ( _lock )
				return _fallback;
		}
	}

	/// <summary>
	/// Makes a map's light volume (and the average light of its objects, added just before) the ones
	/// objects without a row are lit from, until another map is built or activated.
	/// </summary>
	public static void SetVolume( string map, ObjectLightVolume volume )
	{
		if ( volume is null || volume.LitCells == 0 )
		{
			// Not another map's: those objects get the average light instead.
			lock ( _lock )
			{
				_volumes[map] = (null, Vector3.Zero, Vector3.One, Vector4.Zero);
				_fallbacks[map] = _fallback;
				_volume = null;
				_active = map;
				foreach ( var material in _materials )
					Set( material );
			}
			return;
		}

		var textures = volume.Images.Select( image => Texture.Create( volume.AtlasWidth, volume.AtlasHeight, ImageFormat.RGBA8888 )
			.WithData( image )
			.WithStaticUsage()
			.Finish() ).ToArray();

		lock ( _lock )
		{
			_volume = textures;
			_volumeMin = (Vector3)volume.Min * HaloCeMount.WorldScale;
			_volumeCell = (Vector3)volume.CellSize * HaloCeMount.WorldScale;
			_volumeSize = new Vector4( volume.SizeX, volume.SizeY, volume.SizeZ, volume.TilesX );
			_volumes[map] = (_volume, _volumeMin, _volumeCell, _volumeSize);
			_fallbacks[map] = _fallback;
			_active = map;

			foreach ( var material in _materials )
				Set( material );
		}
	}

	/// <summary>
	/// Binds a built map's light volume and average light to the materials, if they aren't
	/// already: the map of the scene being played (see MovingObjectLights).
	/// </summary>
	public static void Activate( string map )
	{
		if ( map is null || string.Equals( map, System.Threading.Volatile.Read( ref _active ), StringComparison.OrdinalIgnoreCase ) )
			return;

		lock ( _lock )
		{
			if ( map is null || string.Equals( map, _active, StringComparison.OrdinalIgnoreCase ) || !_volumes.TryGetValue( map, out var volume ) )
				return;

			(_volume, _volumeMin, _volumeCell, _volumeSize) = volume;
			if ( _fallbacks.TryGetValue( map, out var fallback ) )
				_fallback = fallback;
			_active = map;

			foreach ( var material in _materials )
				Set( material );
		}
	}

	/// <summary>
	/// Gives a model material the table, now and whenever it's rebuilt.
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

	static ObjectLight Average( IReadOnlyList<Entry> entries, ObjectLight fallback )
	{
		if ( entries.Count == 0 )
			return fallback;

		var ambient = System.Numerics.Vector3.Zero;
		var light0 = System.Numerics.Vector3.Zero;
		var toLight0 = System.Numerics.Vector3.Zero;
		var light1 = System.Numerics.Vector3.Zero;
		var toLight1 = System.Numerics.Vector3.Zero;
		var reflection = System.Numerics.Vector3.Zero;
		foreach ( var entry in entries )
		{
			var light = entry.Light;
			ambient += light.Ambient;
			light0 += light.Light0;
			toLight0 += light.ToLight0 * (light.Light0.X + light.Light0.Y + light.Light0.Z);
			light1 += light.Light1;
			toLight1 += light.ToLight1 * (light.Light1.X + light.Light1.Y + light.Light1.Z);
			reflection += light.Reflection;
		}

		static System.Numerics.Vector3 Direction( System.Numerics.Vector3 v, System.Numerics.Vector3 otherwise ) =>
			v.Length() > 1e-4f ? System.Numerics.Vector3.Normalize( v ) : otherwise;

		return new ObjectLight( ambient / entries.Count, light0 / entries.Count, Direction( toLight0, System.Numerics.Vector3.UnitZ ),
			light1 / entries.Count, Direction( toLight1, -System.Numerics.Vector3.UnitZ ), reflection / entries.Count );
	}

	static int RowsFor( int count ) => Math.Max( 1, (count + ObjectsPerRow - 1) / ObjectsPerRow );

	static void Write( int first, IReadOnlyList<Entry> entries )
	{
		for ( var i = 0; i < entries.Count; i++ )
		{
			var index = first + i;
			var light = entries[i].Light;
			var colors = entries[i].ChangeColors;
			_rows[index] = (light, entries[i].Center);
			var offset = ((index / ObjectsPerRow) * Width + (index % ObjectsPerRow) * TexelsPerObject) * 4;

			void Texel( int texel, System.Numerics.Vector3 v, float w )
			{
				var o = offset + texel * 4;
				_data[o] = v.X; _data[o + 1] = v.Y; _data[o + 2] = v.Z; _data[o + 3] = w;
			}

			Texel( 0, light.Ambient, 1 );
			Texel( 1, light.Light0, 0 );
			Texel( 2, light.ToLight0, 0 );
			Texel( 3, light.Reflection, 0 );
			Texel( 4, light.Light1, 0 );
			Texel( 5, light.ToLight1, 0 );
			for ( var c = 0; c < 4; c++ )
				Texel( 6 + c, c < colors.Length ? colors[c] : System.Numerics.Vector3.One, c == 3 ? index : 0 );
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
		material.Set( "g_tObjectTable", _texture ?? MaterialTextures.Transparent );
		material.Set( "g_flObjectCount", _texture is null ? 0f : _count );
		material.Set( "g_vFallbackAmbient", (Vector3)_fallback.Ambient );
		material.Set( "g_vFallbackLight0", (Vector3)_fallback.Light0 );
		material.Set( "g_vFallbackToLight0", (Vector3)_fallback.ToLight0 );
		material.Set( "g_vFallbackLight1", (Vector3)_fallback.Light1 );
		material.Set( "g_vFallbackToLight1", (Vector3)_fallback.ToLight1 );
		material.Set( "g_vDefaultReflectionTint", (Vector3)_fallback.Reflection );

		material.Set( "g_flHaloLightVolume", _volume is null ? 0f : 1f );
		for ( var i = 0; i < 4; i++ )
			material.Set( $"g_tHaloLightVolume{i}", _volume?[i] ?? MaterialTextures.Transparent );
		material.Set( "g_vHaloLightVolumeMin", _volumeMin );
		material.Set( "g_vHaloLightVolumeCell", _volumeCell );
		material.Set( "g_vHaloLightVolumeSize", _volumeSize );
		material.Set( "g_flHaloLightProbeHeight", ProbeHeight );
	}

	/// <summary>
	/// How far above an object's origin (s&amp;box units) the light volume is read when the game
	/// doesn't say where (the HaloLightProbe attribute): a quarter of a world unit, so the ray
	/// starts above the floor a biped stands on.
	/// </summary>
	const float ProbeHeight = 0.25f * HaloCeMount.WorldScale;
}
