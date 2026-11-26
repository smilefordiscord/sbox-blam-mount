using System.Text;
using System.Text.Json;
using Blam.Halo1;
using Blam.Halo1.TagData;
using Blam.Halo1.Tags;
using Blam.Unreal;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// MCC's Halo CE weapon skins (see <see cref="WeaponSkinCatalog"/>), as first person view models a
/// game can swap in for the player's own weapon:
/// <list type="bullet">
/// <item><c>mount://haloce/&lt;weapon tag&gt;_fp_skin_&lt;id&gt;.vmdl</c>: the weapon's view model
/// (<see cref="FirstPersonModel"/>) with the skin, e.g. <c>weapons/pistol/pistol_fp_skin_golden.vmdl</c>.
/// The same model otherwise: bones, attachments and animations.</item>
/// <item><c>mount://haloce/&lt;weapon tag&gt;_fp_skin_&lt;id&gt;_camo.vmdl</c>: its camouflaged
/// variant, the view model's own (skins don't show through the camouflage pass).</item>
/// <item><c>mount://haloce/skins.json</c>: the skins each weapon has, by weapon tag, with MCC's id
/// and name for the weapon (one weapon can have several tags), MCC's default look in HD
/// (<c>default</c>) first:
/// <c>{ "weapons/pistol/pistol.weap": { "Weapon": "pistol", "WeaponName": "Magnum", "Skins": [ { "Id": "golden", "Name": "Golden", "Model": "mount://haloce/weapons/pistol/pistol_fp_skin_golden.vmdl" } ] } }</c></item>
/// </list>
/// </summary>
/// <remarks>
/// Each skin is its own model rather than a material group of the view model, so its textures
/// (2048 square, up to six maps per part) only load for the skin a player picks. Skinned surfaces
/// draw with the model shader's skin variant (<see cref="ModelMaterial.SkinShaderPath"/>): MCC's
/// physically based maps under Halo's object lighting. Skins only reach the first person models:
/// MCC made them for those layouts (the world models draw small LOD bitmaps).
/// </remarks>
sealed class WeaponSkins : IDisposable
{
	public const string ResourceName = "skins.json";

	readonly UnrealPak _pak;
	readonly List<WeaponSkin> _skins;
	readonly Dictionary<string, WeakReference<Texture>> _textures = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, (string Weapon, List<(string Id, string Name, string Model)> Skins)> _byWeapon = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// MCC's names for the weapons, by its id for them.
	/// </summary>
	static readonly Dictionary<string, string> WeaponNames = new( StringComparer.OrdinalIgnoreCase )
	{
		["ASSAULTRIFLE"] = "Assault Rifle",
		["PISTOL"] = "Magnum",
		["PLASMAPISTOL"] = "Plasma Pistol",
		["PLASMARIFLE"] = "Plasma Rifle",
		["SHOTGUN"] = "Shotgun",
		["ROCKETLAUNCHER"] = "Rocket Launcher",
		["SNIPERRIFLE"] = "Sniper Rifle",
		["FLAMETHROWER"] = "Flamethrower",
		["NEEDLER"] = "Needler",
		["FUELRODGUN"] = "Fuel Rod Gun",
	};

	WeaponSkins( UnrealPak pak, List<WeaponSkin> skins )
	{
		_pak = pak;
		_skins = skins;
	}

	/// <summary>
	/// Opens MCC's pak beside the game folder and reads its skins. Null (and a warning) if it can't.
	/// </summary>
	public static WeaponSkins Open( string gameDirectory, Sandbox.Diagnostics.Logger log )
	{
		var path = System.IO.Path.Combine( System.IO.Path.GetDirectoryName( gameDirectory ), "mcc", "Content", "Paks", "MCC-WindowsNoEditor.pak" );
		if ( !File.Exists( path ) )
		{
			log.Warning( $"Couldn't find {path}, so no weapon skins" );
			return null;
		}

		UnrealPak pak = null;
		try
		{
			pak = new UnrealPak( path );
			return new WeaponSkins( pak, WeaponSkinCatalog.Read( pak ) );
		}
		catch ( Exception e )
		{
			pak?.Dispose();
			log.Warning( e, $"Couldn't read weapon skins from {path}: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// The skins that reach a first person weapon model: those with a texture set for one of the
	/// bitmaps its model shaders draw.
	/// </summary>
	public IEnumerable<WeaponSkin> For( CacheFile cache, FirstPersonWeapon weapon )
	{
		var model = new GbxModelTag( cache, weapon.Model );
		var bitmaps = model.Shaders
			.Where( s => s.Shader?.Class == TagClasses.ShaderModel )
			.Select( s => new ShaderModelTag( cache, s.Shader ).BaseMap?.Path )
			.Where( p => p is not null )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		return _skins.Where( s => s.Maps.Keys.Any( bitmaps.Contains ) );
	}

	/// <summary>
	/// Registers a weapon's skinned view models, once per weapon.
	/// </summary>
	public void Register( MountContext context, string ident, CacheFile cache, TagEntry weaponTag, FirstPersonWeapon weapon, string viewModelName, Sandbox.Diagnostics.Logger log )
	{
		var key = TagJsonWriter.TagName( weaponTag );
		if ( _byWeapon.ContainsKey( key ) )
			return;

		var list = new List<(string, string, string)>();
		try
		{
			foreach ( var skin in For( cache, weapon ) )
			{
				var name = ResourceNameFor( viewModelName, skin.Id );
				context.Add( ResourceType.Model, name, new FirstPersonModelLoader( cache.FilePath, weaponTag.Id, skin: skin ) );
				context.Add( ResourceType.Model, GbxModelLoader.CamoResourceName( name ), new FirstPersonModelLoader( cache.FilePath, weaponTag.Id, camouflaged: true ) );
				list.Add( (skin.Id, skin.Name, $"mount://{ident}/{name}.vmdl") );
				_byWeapon[key] = (skin.Weapon, list);
			}
		}
		catch ( Exception e )
		{
			// Never in the way of the rest of the map's resources.
			log.Warning( e, $"Couldn't register skins for {weaponTag.Path}: {e.Message}" );
		}
	}

	/// <summary>
	/// Registers <c>skins.json</c>. Call after every weapon's <see cref="Register"/>.
	/// </summary>
	public void RegisterList( MountContext context )
	{
		var json = new MemoryStream();
		using ( var writer = new Utf8JsonWriter( json, new JsonWriterOptions { Indented = true } ) )
		{
			writer.WriteStartObject();
			foreach ( var (tag, (weapon, skins)) in _byWeapon.Where( x => x.Value.Skins.Count > 0 ).OrderBy( x => x.Key, StringComparer.Ordinal ) )
			{
				writer.WriteStartObject( tag );
				writer.WriteString( "Weapon", weapon.ToLowerInvariant() );
				writer.WriteString( "WeaponName", WeaponNames.GetValueOrDefault( weapon ) ?? weapon );
				writer.WriteStartArray( "Skins" );
				foreach ( var (id, name, model) in skins )
				{
					writer.WriteStartObject();
					writer.WriteString( "Id", id );
					writer.WriteString( "Name", name );
					writer.WriteString( "Model", model );
					writer.WriteEndObject();
				}
				writer.WriteEndArray();
				writer.WriteEndObject();
			}
			writer.WriteEndObject();
		}

		context.Add( ResourceType.Text, ResourceName, new TextLoader( Encoding.UTF8.GetString( json.ToArray() ) ) );
	}

	public static string ResourceNameFor( string viewModelName, string skinId ) => $"{viewModelName}_skin_{skinId}";

	/// <summary>
	/// A skin texture (a texture package path in the pak), shared while anything uses it. Null and
	/// a warning if it can't be read.
	/// </summary>
	public Texture LoadTexture( string path, Sandbox.Diagnostics.Logger log )
	{
		if ( path is null )
			return null;

		lock ( _textures )
		{
			if ( _textures.TryGetValue( path, out var weak ) && weak.TryGetTarget( out var cached ) && cached.IsValid() )
				return cached;
		}

		Texture texture;
		try
		{
			texture = CreateTexture( path );
		}
		catch ( Exception e )
		{
			log.Warning( e, $"Couldn't load the skin texture {path}: {e.Message}" );
			return null;
		}

		if ( texture is null )
			return null;

		lock ( _textures )
			_textures[path] = new WeakReference<Texture>( texture );

		return texture;
	}

	Texture CreateTexture( string path )
	{
		var texture = new UnrealTexture2D( WeaponSkinCatalog.ReadPackage( _pak, path ) );
		ImageFormat? format = texture.PixelFormat switch
		{
			"PF_DXT1" => ImageFormat.DXT1,
			"PF_DXT5" => ImageFormat.DXT5,
			"PF_BC5" => ImageFormat.ATI2N,
			"PF_BC4" => ImageFormat.ATI1N,
			"PF_BC7" => ImageFormat.BC7,
			"PF_B8G8R8A8" => ImageFormat.BGRA8888,
			"PF_G8" => ImageFormat.I8,
			_ => null,
		};
		if ( format is null )
			throw new NotSupportedException( $"pixel format {texture.PixelFormat}" );

		// Full mip levels, halving from the top; block compressed ones down to 4x4 (the pak repeats
		// 4x4 for the smaller ones).
		var blocks = format is ImageFormat.DXT1 or ImageFormat.DXT5 or ImageFormat.ATI2N or ImageFormat.ATI1N or ImageFormat.BC7;
		var levels = new List<byte[]>();
		var bulk = _pak.Find( System.IO.Path.ChangeExtension( path, ".ubulk" ) );
		for ( var i = 0; i < texture.Mips.Count; i++ )
		{
			var mip = texture.Mips[i];
			if ( mip.Width != Math.Max( 1, texture.Width >> i ) || mip.Height != Math.Max( 1, texture.Height >> i ) || (blocks && Math.Min( mip.Width, mip.Height ) < 4) )
				break;

			levels.Add( mip.Inline ?? (bulk is null ? null : _pak.Read( bulk, mip.BulkOffset, mip.BulkSize )) );
			if ( levels[^1] is null )
				throw new FileNotFoundException( $"{path}: mip {i} is in a .ubulk that isn't in the pak" );
		}

		// Smallest level first, as the texture builder takes them.
		var output = new byte[levels.Sum( l => l.Length )];
		var destination = 0;
		for ( var level = levels.Count - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( output, destination );
			destination += levels[level].Length;
		}

		return Texture.Create( texture.Width, texture.Height, format.Value )
			.WithName( $"mcc/{System.IO.Path.GetFileNameWithoutExtension( path )}" )
			.WithData( output )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}

	readonly Dictionary<string, float> _albedoScales = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// What a skin part's diffuse color is multiplied by (in linear light) to sit at the brightness
	/// of the Halo bitmap it replaces: the Halo bitmap's average luminance over that of the weapon's
	/// default skin, so every skin of a weapon is scaled alike (Golden stays brighter than Black
	/// Cherry). MCC's maps are physically based colors, with the shading Halo's textures had painted
	/// in left to the lighting; under Halo's lighting, made for its darker textures, they came out
	/// washed out (the assault rifle's 1.6 times Halo's average). 1 when either can't be read.
	/// </summary>
	public float AlbedoScale( HaloCeMount host, CacheFile cache, TagEntry haloBitmap, WeaponSkinMaps maps )
	{
		var reference = maps.DefaultDiffuse ?? maps.Diffuse;
		if ( haloBitmap is null || reference is null )
			return 1;

		var key = $"{haloBitmap.Path}|{reference}";
		lock ( _albedoScales )
		{
			if ( _albedoScales.TryGetValue( key, out var cached ) )
				return cached;
		}

		var scale = 1f;
		try
		{
			var halo = HaloLuminance( host, cache, haloBitmap );
			var skin = SkinLuminance( reference );
			if ( halo > 0 && skin > 0 )
				scale = Math.Clamp( halo / skin, 0.25f, 1f );
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"Couldn't compare {reference} with {haloBitmap.Path}: {e.Message}" );
		}

		lock ( _albedoScales )
			_albedoScales[key] = scale;

		return scale;
	}

	static float HaloLuminance( HaloCeMount host, CacheFile cache, TagEntry bitmapTag )
	{
		var bitmap = new BitmapTag( cache, bitmapTag ).Bitmaps.FirstOrDefault();
		if ( bitmap is null || bitmap.Type != BitmapType.Texture2D )
			return 0;

		var rgba = BitmapDecoder.DecodeTopLevel( bitmap, BitmapTextureLoader.ReadPixels( host, cache.FilePath, bitmap ) );
		return rgba is null ? 0 : MeanLuminance( rgba );
	}

	/// <summary>The average of the texture's smallest stored level at least 32 square (in the .uexp).</summary>
	float SkinLuminance( string path )
	{
		var texture = new UnrealTexture2D( WeaponSkinCatalog.ReadPackage( _pak, path ) );
		var mip = texture.Mips.LastOrDefault( m => m.Inline is not null && m.Width >= 32 && m.Height >= 32 );
		if ( mip is null )
			return 0;

		var rgba = new byte[mip.Width * mip.Height * 4];
		switch ( texture.PixelFormat )
		{
			case "PF_DXT1": BitmapDecoder.Dxt1ToRgba8888( mip.Inline, rgba, mip.Width, mip.Height ); break;
			case "PF_DXT5": BitmapDecoder.Dxt5ToRgba8888( mip.Inline, rgba, mip.Width, mip.Height ); break;
			default: return 0;
		}

		return MeanLuminance( rgba );
	}

	static float MeanLuminance( byte[] rgba )
	{
		double sum = 0;
		var count = rgba.Length / 4;
		for ( var i = 0; i < count; i++ )
			sum += 0.2126 * ToLinear( rgba[i * 4] ) + 0.7152 * ToLinear( rgba[i * 4 + 1] ) + 0.0722 * ToLinear( rgba[i * 4 + 2] );
		return count > 0 ? (float)(sum / count) : 0;
	}

	static double ToLinear( byte value )
	{
		var c = value / 255.0;
		return c <= 0.04045 ? c / 12.92 : Math.Pow( (c + 0.055) / 1.055, 2.4 );
	}

	public void Dispose() => _pak.Dispose();

	sealed class TextLoader( string text ) : HaloCeLoader
	{
		protected override object Load() => text;
	}
}
