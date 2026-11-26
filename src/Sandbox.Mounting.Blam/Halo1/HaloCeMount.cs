using Blam.Halo1;
using Blam.Halo1.Tags;
using Blam.IO;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Halo: Combat Evolved, from <c>halo1/maps</c> in MCC.
/// </summary>
public sealed class HaloCeMount : MccMount
{
	// s&box units per Halo world unit (WorldUnits).
	public const float WorldScale = global::Blam.WorldUnits.SboxScale;

	/// <summary>
	/// The mounted instance. Loaders reach the mount through this.
	/// </summary>
	internal static HaloCeMount Current { get; private set; }

	public override string Ident => "haloce";
	public override string Title => "Halo: Combat Evolved";
	protected override string GameFolder => "halo1";

	/// <summary>
	/// Shared resource maps that sit beside the scenario maps but aren't scenarios themselves.
	/// </summary>
	static readonly HashSet<string> ResourceMapNames = new( StringComparer.OrdinalIgnoreCase ) { "bitmaps", "sounds", "loc" };

	readonly Dictionary<string, RawFile> _mapFiles = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, Lazy<CacheFile>> _caches = new( StringComparer.OrdinalIgnoreCase );

	internal ResourceMap Bitmaps { get; private set; }

	/// <summary>
	/// Sound tags and the FMOD banks their samples are in, or null if the banks are missing.
	/// </summary>
	internal HaloSounds Sounds { get; private set; }

	/// <summary>
	/// MCC's weapon skins, or null if its Unreal pak couldn't be read.
	/// </summary>
	internal WeaponSkins WeaponSkins { get; private set; }

	/// <summary>
	/// The mount's logger, for loaders to report problems with.
	/// </summary>
	internal Sandbox.Diagnostics.Logger Logger => Log;

	protected override Task Mount( MountContext context )
	{
		Current = this;
		MccBrightness.Start();
		SceneBuilding.Register();

		var mapsDirectory = System.IO.Path.Combine( GameDirectory, "maps" );
		var bitmapsPath = System.IO.Path.Combine( mapsDirectory, "bitmaps.map" );
		if ( !File.Exists( bitmapsPath ) )
		{
			Log.Warning( $"Couldn't find {bitmapsPath}" );
			return Task.CompletedTask;
		}

		Bitmaps = new ResourceMap( bitmapsPath );
		Sounds = HaloSounds.Open( GameDirectory, Log );
		WeaponSkins = WeaponSkins.Open( GameDirectory, Log );

		// P8 bump maps index a normal palette that only exists in the game's own code.
		BumpPalette.Rgba = BumpPalette.Find( System.IO.Path.Combine( GameDirectory, "halo1.dll" ) );
		if ( BumpPalette.Rgba is null )
			Log.Warning( "Couldn't find the bump map palette in halo1.dll, P8 bump maps will be flat" );

		// Most tags (HUD, weapons, characters) are in every map, and in practice they're the same
		// in each. The first map to contain a tag provides it.
		var registeredTextures = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var registeredModels = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var registeredSkies = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var registeredDecals = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var registeredEffects = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var registeredMaterials = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var objectModels = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var sandboxWeapons = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		HaloSurfaces.Register( context );

		// Tag data as JSON for game code, each tag from the first multiplayer map that has it.
		var tagData = new TagDataResources();

		foreach ( var mapPath in System.IO.Directory.EnumerateFiles( mapsDirectory, "*.map" ).Order() )
		{
			if ( ResourceMapNames.Contains( System.IO.Path.GetFileNameWithoutExtension( mapPath ) ) )
				continue;

			try
			{
				using var cache = CacheFile.Open( mapPath, Bitmaps );
				RegisterBitmaps( context, cache, registeredTextures );
				RegisterFonts( context, cache, registeredTextures );
				RegisterModels( context, cache, registeredModels );
				PlayerModels.Register( context, Ident, cache, registeredModels );
				CollectObjectModels( cache, objectModels );
				RegisterSkies( context, cache, registeredSkies );
				DecalPrefabs.Register( context, cache, registeredDecals, Log );
				RegisterOverlayMaterials( context, cache, registeredMaterials );
				FlagCloth.Register( context, cache, registeredModels, registeredMaterials );
				Sounds?.Register( context, cache );
				tagData.Collect( cache );
				EffectPrefabs.Register( context, cache, registeredEffects );
				RegisterSandboxWeapons( context, cache, sandboxWeapons );

				if ( cache.ScenarioType != ScenarioType.UserInterface )
					context.Add( ResourceType.Scene, $"maps/{cache.Name}", new ScenarioSceneLoader( mapPath ) );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Skipping {mapPath}: {e.Message}" );
			}
		}

		tagData.Register( context );
		WeaponSkins?.RegisterList( context );
		ShowInSpawnMenus( objectModels );

		IsMounted = true;
		return Task.CompletedTask;
	}

	void RegisterBitmaps( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.Bitmap || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = ResourcePath( tag.Path );
			if ( !registered.Add( path ) )
				continue;

			var bitmapTag = new BitmapTag( cache, tag );
			var loaders = new BitmapTextureLoader[bitmapTag.Bitmaps.Count];
			foreach ( var bitmap in bitmapTag.Bitmaps )
			{
				if ( !BitmapTextureLoader.IsSupported( bitmap ) )
					continue;

				loaders[bitmap.Index] = new BitmapTextureLoader( cache.FilePath, bitmap );
				context.Add( ResourceType.Texture, TextureName( tag.Path, bitmap.Index ), loaders[bitmap.Index] );
			}

			RegisterFrames( context, cache, bitmapTag, loaders );
		}
	}

	/// <summary>
	/// Registers every sequence frame on its own (<see cref="FrameTextureName"/>): each sprite of a
	/// sprite tag cropped out of its page, and each bitmap of a 2D or interface bitmap tag's
	/// sequences as the bitmap's texture. The HUD and menus pick images by bitmap tag, sequence and
	/// frame, so this gives a game one name for all of them. Nothing is decoded until it's loaded.
	/// </summary>
	static void RegisterFrames( MountContext context, CacheFile cache, BitmapTag bitmapTag, BitmapTextureLoader[] loaders )
	{
		var tagPath = bitmapTag.Tag.Path;
		for ( var s = 0; s < bitmapTag.Sequences.Count; s++ )
		{
			var sequence = bitmapTag.Sequences[s];
			if ( sequence.Sprites.Count > 0 )
			{
				for ( var i = 0; i < sequence.Sprites.Count; i++ )
				{
					var sprite = sequence.Sprites[i];
					if ( sprite.BitmapIndex < 0 || sprite.BitmapIndex >= bitmapTag.Bitmaps.Count )
						continue;

					var page = bitmapTag.Bitmaps[sprite.BitmapIndex];
					if ( SpriteCrop.CanCrop( page ) )
						context.Add( ResourceType.Texture, FrameTextureName( tagPath, s, i ), new SpriteTextureLoader( cache.FilePath, page, sprite.GetPixelRect( page ) ) );
				}
			}
			else if ( bitmapTag.Kind is BitmapTagType.Textures2D or BitmapTagType.InterfaceBitmaps )
			{
				for ( var i = 0; i < sequence.BitmapCount; i++ )
				{
					var index = sequence.FirstBitmapIndex + i;
					if ( index >= 0 && index < loaders.Length && loaders[index] is not null )
						context.Add( ResourceType.Texture, FrameTextureName( tagPath, s, i ), new FrameTextureLoader( loaders[index] ) );
				}
			}
		}
	}

	void RegisterFonts( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != FontTag.Class || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = FontTextureName( tag.Path );
			if ( registered.Add( path ) )
				context.Add( ResourceType.Texture, path, new FontTextureLoader( cache.FilePath, tag.Id ) );
		}
	}

	void RegisterModels( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.GbxModel || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = ResourcePath( tag.Path );
			if ( !registered.Add( path ) )
				continue;

			context.Add( ResourceType.Model, path, new GbxModelLoader( cache.FilePath, tag.Id ) );

			// Its variant for a camouflaged object (see GbxModelBuilder.Meshes.Camouflaged), only built when loaded.
			if ( registered.Add( GbxModelLoader.CamoResourceName( path ) ) )
				context.Add( ResourceType.Model, GbxModelLoader.CamoResourceName( path ), new GbxModelLoader( cache.FilePath, tag.Id, camouflaged: true ) );

			// Models with glass again in parts, and as a prefab drawing them in the right order.
			ObjectGlass.Register( context, cache, tag, Log );
		}

		// Each model again with every shader permutation an object or actor variant forces on it. Only built when loaded.
		foreach ( var (model, permutation) in ObjectTags.ForcedShaderPermutations( cache ) )
		{
			var path = GbxModelLoader.PermutationResourceName( model.Path, permutation );
			if ( registered.Add( path ) )
				context.Add( ResourceType.Model, path, new GbxModelLoader( cache.FilePath, model.Id, forcedShaderPermutation: permutation ) );
		}

		// The depth prepass of every model whose object's transparency self-occludes.
		ObjectSelfOcclusion.Register( context, cache, registered );

		// Each biped's model again with its aiming baked into sequences, for games to animate units
		// that aim (see AimPoses). Only built when loaded.
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class.ToString() != "bipd" || ObjectTags.GetObjectModel( cache, tag ) is not { } model || model.Class != TagClasses.GbxModel || string.IsNullOrEmpty( model.Path ) )
				continue;

			var path = GbxModelLoader.AimResourceName( model.Path );
			if ( registered.Add( path ) )
			{
				context.Add( ResourceType.Model, path, new GbxModelLoader( cache.FilePath, model.Id, aimPoses: true ) );
				context.Add( ResourceType.Model, GbxModelLoader.CamoResourceName( path ), new GbxModelLoader( cache.FilePath, model.Id, aimPoses: true, camouflaged: true ) );
			}
		}

		// Each weapon's first person view model: hands and weapon on the first person graph.
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != FirstPersonWeapon.WeaponClass || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = FirstPersonModel.ResourceName( tag.Path );
			if ( !registered.Contains( path ) && FirstPersonWeapon.Find( cache, tag ) is { } weapon && registered.Add( path ) )
			{
				context.Add( ResourceType.Model, path, new FirstPersonModelLoader( cache.FilePath, tag.Id ) );
				context.Add( ResourceType.Model, GbxModelLoader.CamoResourceName( path ), new FirstPersonModelLoader( cache.FilePath, tag.Id, camouflaged: true ) );

				// Again with each of MCC's skins for it (see WeaponSkins).
				WeaponSkins?.Register( context, Ident, cache, tag, weapon, path, Log );
			}
		}
	}

	// Halo's multiplayer weapons as Sandbox weapons (HaloWeapons.SandboxWeapons), each from the first map that has it, and every other
	// hand-held weapon unlisted, for game code spawning a map's weapons by tag.
	void RegisterSandboxWeapons( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		var listed = HaloWeapons.SandboxWeapons.HaloCe.ToDictionary( e => HaloWeapons.SandboxWeapons.CachePath( e.Tag ), StringComparer.OrdinalIgnoreCase );
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class.ToString() != "weap" || string.IsNullOrEmpty( tag.Path ) || tag.Path.StartsWith( "vehicles\\", StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( ObjectTags.GetObjectModel( cache, tag ) is not { } model || string.IsNullOrEmpty( model.Path ) )
				continue;

			var isListed = listed.TryGetValue( tag.Path, out var entry );
			entry ??= HaloWeapons.SandboxWeapons.Unlisted( $"{ResourcePath( tag.Path )}.weap" );
			if ( !registered.Add( HaloWeapons.SandboxWeapons.PrefabPath( entry.Tag ) ) )
				continue;

			HaloWeapons.SandboxWeapons.Register( context, entry, $"mount://{Ident}/{ResourcePath( model.Path )}.vmdl", halo2: false, isListed );
		}
	}

	static readonly HashSet<string> ObjectClasses = ["bipd", "vehi", "weap", "eqip", "garb", "proj", "scen", "mach", "ctrl", "lifi", "plac", "ssce"];

	// The models objects are drawn with, the ones worth spawning on their own.
	static void CollectObjectModels( CacheFile cache, HashSet<string> models )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( ObjectClasses.Contains( tag.Class.ToString() ) && ObjectTags.GetObjectModel( cache, tag ) is { } model && model.Class == TagClasses.GbxModel && !string.IsNullOrEmpty( model.Path ) )
				models.Add( ResourcePath( model.Path ) );
		}
	}

	void RegisterSkies( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.Sky || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = ResourcePath( tag.Path );
			if ( registered.Add( path ) )
				context.Add( ResourceType.Material, path, new SkyMaterialLoader( cache.FilePath, tag.Id ) );
		}
	}

	/// <summary>
	/// Materials a game draws over its own models: each plasma shader (the shield shells of elites,
	/// the cyborg and the monitor) at its tag path, and the active camouflage, once.
	/// </summary>
	void RegisterOverlayMaterials( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.ShaderTransparentPlasma || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = ResourcePath( tag.Path );
			if ( registered.Add( path ) )
				context.Add( ResourceType.Material, path, new PlasmaMaterialLoader( cache.FilePath, tag.Id ) );
		}

		if ( registered.Add( ActiveCamouflageMaterial.ResourceName ) )
			context.Add( ResourceType.Material, ActiveCamouflageMaterial.ResourceName, new ActiveCamouflageMaterialLoader( cache.FilePath ) );
	}

	/// <summary>
	/// Full path to load a sky tag's SkyBox2D material with.
	/// </summary>
	internal static string SkyMaterialPath( string ident, string tagPath ) => $"mount://{ident}/{ResourcePath( tagPath )}.vmat";

	/// <summary>
	/// Full path to load a gbxmodel's model with.
	/// </summary>
	internal static string ModelPath( string ident, string tagPath ) => $"mount://{ident}/{ResourcePath( tagPath )}.vmdl";

	/// <summary>
	/// Opens a map for reading tags, or returns it if it's already open. Opening reads the map's
	/// whole tag data, so maps stay open once something has needed them.
	/// </summary>
	internal CacheFile GetCache( string mapPath )
	{
		Lazy<CacheFile> cache;
		lock ( _caches )
		{
			if ( !_caches.TryGetValue( mapPath, out cache ) )
				_caches[mapPath] = cache = new Lazy<CacheFile>( () => CacheFile.Open( mapPath, Bitmaps ) );
		}

		return cache.Value;
	}

	/// <summary>
	/// Tag paths use backslashes, e.g. <c>levels\test\bloodgulch\bitmaps\grass</c>.
	/// </summary>
	internal static string ResourcePath( string tagPath ) => tagPath.Replace( '\\', '/' );

	/// <summary>
	/// The resource name of one bitmap in a bitmap tag. Sprite sheets, animated textures and
	/// lightmaps hold several bitmaps in one tag, and the ones after the first get an index suffix.
	/// </summary>
	internal static string TextureName( string tagPath, int index ) =>
		index == 0 ? ResourcePath( tagPath ) : $"{ResourcePath( tagPath )}_{index}";

	/// <summary>
	/// Full path to load a bitmap's texture with.
	/// </summary>
	internal static string TexturePath( string ident, string tagPath, int index ) =>
		$"mount://{ident}/{TextureName( tagPath, index )}.vtex";

	/// <summary>
	/// The resource name of one frame of a bitmap tag's sequence: <c>&lt;tag path&gt;/&lt;sequence&gt;_&lt;frame&gt;</c>,
	/// both indices from 0. For sprite tags the frame is the sequence's sprite, cropped to its own
	/// texture; for 2D and interface bitmap tags it's the sequence's bitmap (first bitmap + frame).
	/// The tag path becomes a folder, so these can't collide with the whole bitmap names
	/// (<see cref="TextureName"/>), and the game computes them from what HUD tags store: a bitmap
	/// reference, a sequence index and a frame.
	/// </summary>
	internal static string FrameTextureName( string tagPath, int sequence, int frame ) =>
		$"{ResourcePath( tagPath )}/{sequence}_{frame}";

	/// <summary>
	/// The resource name of a font's glyph page: <c>&lt;font tag path&gt;/page</c>.
	/// </summary>
	internal static string FontTextureName( string tagPath ) => $"{ResourcePath( tagPath )}/page";

	/// <summary>
	/// Reads raw bytes from a map file. Handles stay open until shutdown, since loaders read from
	/// the same few maps over and over.
	/// </summary>
	internal byte[] ReadMap( string mapPath, long offset, int length )
	{
		RawFile file;
		lock ( _mapFiles )
		{
			if ( !_mapFiles.TryGetValue( mapPath, out file ) )
				_mapFiles[mapPath] = file = new RawFile( mapPath );
		}

		return file.Read( offset, length );
	}

	protected override void Shutdown()
	{
		MccBrightness.Stop();
		MirrorReflections.StopWatching();
		PlanarFogFrame.StopWatching();
		MovingObjectLights.StopWatching();
		ModelDetailLevels.StopWatching();
		ObjectShadowVolumes.StopWatching();
		ClusterVisibility.StopWatching();
		HaloParticleWorld.StopWatching();
		HaloEffectLibrary.Clear();
		ParticleSprites.StopWatching();

		lock ( _mapFiles )
		{
			foreach ( var file in _mapFiles.Values )
				file.Dispose();

			_mapFiles.Clear();
		}

		lock ( _caches )
		{
			foreach ( var cache in _caches.Values.Where( x => x.IsValueCreated ) )
				cache.Value.Dispose();

			_caches.Clear();
		}

		Bitmaps?.Dispose();
		Bitmaps = null;

		Sounds?.Dispose();
		Sounds = null;

		WeaponSkins?.Dispose();
		WeaponSkins = null;

		if ( Current == this )
			Current = null;
	}
}
