using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2 (classic graphics), from <c>halo2/h2_maps_win64_dx11</c> in MCC.
/// </summary>
public sealed class Halo2Mount : MccMount
{
	/// <summary>
	/// s&amp;box units per Halo world unit: the same as the Halo CE mount (Halo 2 keeps CE's units; the Master
	/// Chief is about 0.7 world units tall in both), so levels of both games match a player of the same size.
	/// </summary>
	public const float WorldScale = Halo1.HaloCeMount.WorldScale;

	/// <summary>
	/// The mounted instance. Loaders reach the mount through this rather than through
	/// <see cref="ResourceLoader{T}.Host"/>, which is the development reload stub during development.
	/// </summary>
	internal static Halo2Mount Current { get; private set; }

	public override string Ident => "halo2";
	public override string Title => "Halo 2";
	protected override string GameFolder => "halo2";

	/// <summary>
	/// Cache files beside the scenario maps that aren't scenarios. Nothing in MCC's maps points into
	/// single_player_shared.map; shared.map holds the tags every multiplayer map shares.
	/// </summary>
	static readonly HashSet<string> ResourceMapNames = new( StringComparer.OrdinalIgnoreCase ) { "shared", "single_player_shared" };

	internal TextureFile Textures { get; private set; }

	/// <summary>
	/// The sound tags as sound events and files, or null without the sample files.
	/// </summary>
	internal Halo2Sounds Sounds { get; private set; }

	string _sharedPath;
	readonly Dictionary<string, Lazy<CacheFile>> _caches = new( StringComparer.OrdinalIgnoreCase );

	internal Sandbox.Diagnostics.Logger Logger => Log;

	protected override Task Mount( MountContext context )
	{
		Current = this;
		MccBrightness.Start();

		var mapsDirectory = System.IO.Path.Combine( GameDirectory, "h2_maps_win64_dx11" );
		var sharedPath = System.IO.Path.Combine( mapsDirectory, "shared.map" );
		var texturesPath = System.IO.Path.Combine( mapsDirectory, "textures.dat" );
		if ( !File.Exists( sharedPath ) || !File.Exists( texturesPath ) )
		{
			Log.Warning( $"Couldn't find shared.map and textures.dat in {mapsDirectory}" );
			return Task.CompletedTask;
		}

		Textures = new TextureFile( texturesPath );
		_sharedPath = sharedPath;

		// The engine's periodic function shapes (flickering lights...), from the install's halo2.dll.
		if ( !global::Blam.Halo2.PeriodicTables.TryLoad( System.IO.Path.Combine( GameDirectory, "halo2.dll" ) ) )
			Log.Warning( "Couldn't read the periodic function tables from halo2.dll; using approximations" );
		var shared = GetCache( sharedPath );
		Halo2Surfaces.Register( context, shared );

		// Multiplayer maps take most of their tags from shared.map; the first map to contain a tag provides it.
		var registeredTextures = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		RegisterBitmaps( context, shared, registeredTextures );
		var models = new Dictionary<string, (string MapPath, uint Id, int Variants)>( StringComparer.OrdinalIgnoreCase );
		var modelFunctions = new Dictionary<string, IReadOnlyList<string>>( StringComparer.OrdinalIgnoreCase );
		var physicsModels = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		FindModels( shared, models, modelFunctions, physicsModels );
		var overlays = new ModelOverlayMaterials();
		overlays.Find( shared );
		var animationData = new Halo2AnimationData();
		animationData.Collect( shared );
		var firstPerson = new FirstPersonViews();
		firstPerson.Collect( shared );
		var firstPersonBodies = new FirstPersonBodies();
		firstPersonBodies.Collect( shared );
		var tagData = new TagDataResources();
		tagData.Collect( shared );
		Sounds = Halo2Sounds.Open( mapsDirectory, Log );
		Sounds?.Register( context, shared, Log );
		var registeredEffects = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		Halo2Effects.Register( context, shared, registeredEffects );
		LightVolumeModels.Register( context, shared, registeredEffects );
		LensFlareModels.Register( context, shared, registeredEffects );

		var sandboxWeapons = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var mapPath in System.IO.Directory.EnumerateFiles( mapsDirectory, "*.map" ).Order() )
		{
			if ( ResourceMapNames.Contains( System.IO.Path.GetFileNameWithoutExtension( mapPath ) ) )
				continue;

			try
			{
				using var cache = CacheFile.Open( mapPath, shared );
				RegisterBitmaps( context, cache, registeredTextures );
				FindModels( cache, models, modelFunctions, physicsModels );
				overlays.Find( cache );
				animationData.Collect( cache );
				firstPerson.Collect( cache );
				firstPersonBodies.Collect( cache );
				tagData.Collect( cache );
				Sounds?.Register( context, cache, Log );
				Halo2Effects.Register( context, cache, registeredEffects );
				LightVolumeModels.Register( context, cache, registeredEffects );
				LensFlareModels.Register( context, cache, registeredEffects );
				RegisterSandboxWeapons( context, cache, sandboxWeapons );

				if ( cache.Type != CacheType.MainMenu )
					context.Add( ResourceType.Scene, $"maps/{cache.Name}", new ScenarioSceneLoader( mapPath ) );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Skipping {mapPath}: {e.Message}" );
			}
		}

		foreach ( var (path, (mapPath, id, _)) in models )
		{
			if ( modelFunctions.TryGetValue( path, out var functions ) )
				DrivenParameters.RegisterSlots( context, path, functions );
			context.Add( ResourceType.Model, path, new RenderModelLoader( mapPath, id ) );
			context.Add( ResourceType.Model, CamoModelLoader.ResourceName( path ), new CamoModelLoader( mapPath, id ) );
			if ( physicsModels.Contains( path ) )
				context.Add( ResourceType.Model, PhysicsModelName( path ), new RenderModelLoader( mapPath, id, physics: true ) );
		}
		overlays.Register( context );
		animationData.Register( context );
		firstPerson.Register( context );
		firstPersonBodies.Register( context );
		var fontTable = Halo2Fonts.Table( GameDirectory );
		Halo2Fonts.Register( context, GameDirectory, fontTable );
		tagData.Register( context, sharedPath, fontTable );
		Halo2ParticleWorld.Watch();

		// Crates and bipeds spawn with Halo's physics.
		ShowInSpawnMenus( models.Keys.Select( x => physicsModels.Contains( x ) ? PhysicsModelName( x ) : x ) );

		IsMounted = true;
		return Task.CompletedTask;
	}

	static void RegisterBitmaps( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != BitmapTag.Class || string.IsNullOrEmpty( tag.Path ) || tag.Cache != cache )
				continue;

			if ( !registered.Add( ResourcePath( tag.Path ) ) )
				continue;

			var bitmapTag = new BitmapTag( tag );
			var loaders = new BitmapTextureLoader[bitmapTag.Bitmaps.Count];
			foreach ( var bitmap in bitmapTag.Bitmaps )
			{
				if ( !BitmapTextureLoader.IsSupported( bitmap ) )
					continue;

				loaders[bitmap.Index] = new BitmapTextureLoader( bitmap );
				context.Add( ResourceType.Texture, TextureName( tag.Path, bitmap.Index ), loaders[bitmap.Index] );
			}

			RegisterFrames( context, bitmapTag, loaders );
		}
	}

	// Registers every sequence frame on its own (FrameTextureName): a sprite cropped out of its page, or a sequence's bitmap.
	// The HUD picks images by bitmap, sequence and frame, so this gives a game one name for all of them.
	// Nothing is decoded until it's loaded.
	static void RegisterFrames( MountContext context, BitmapTag bitmapTag, BitmapTextureLoader[] loaders )
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
					if ( SpriteFrameLoader.CanCrop( page ) )
						context.Add( ResourceType.Texture, FrameTextureName( tagPath, s, i ), new SpriteFrameLoader( page, sprite.GetPixelRect( page ) ) );
				}
			}
			else
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

	/// <summary>
	/// Every model tag (<c>hlmt</c>) to register as <c>mount://halo2/&lt;hlmt path&gt;.vmdl</c> (see
	/// <see cref="RenderModelBuilder"/> for why per hlmt and not per render model). Campaign maps leave out the
	/// variants they don't use (the marine's has 20 in 00a_introduction, 6 in 08b_deltacontrol), so the copy
	/// with the most variants provides it, the first of those.
	/// </summary>
	// Also notes each model's function slots (ShaderFunctions), where its shaders follow any, and the models with Halo's physics.
	// Those are crates' models, and for their ragdolls the models of the bipeds in shared.map and the multiplayer maps.
	// Halo 2's multiplayer weapons as Sandbox weapons (HaloWeapons.SandboxWeapons), each from the first map that has it.
	void RegisterSandboxWeapons( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var entry in HaloWeapons.SandboxWeapons.Halo2 )
		{
			if ( registered.Contains( entry.Tag ) )
				continue;

			var path = HaloWeapons.SandboxWeapons.CachePath( entry.Tag );
			var tag = cache.Tags.FirstOrDefault( t => t.Class.ToString() == "weap" && t.Address != 0 && string.Equals( t.Path, path, StringComparison.OrdinalIgnoreCase ) );
			if ( tag is null || !ObjectTag.IsObject( tag ) || new ObjectTag( tag ).Model is not { Address: not 0 } model || string.IsNullOrEmpty( model.Path ) )
				continue;

			registered.Add( entry.Tag );
			HaloWeapons.SandboxWeapons.Register( context, entry, $"mount://{Ident}/{ResourcePath( model.Path )}.vmdl", halo2: true );
		}
	}

	static void FindModels( CacheFile cache, Dictionary<string, (string MapPath, uint Id, int Variants)> models, Dictionary<string, IReadOnlyList<string>> functions,
		HashSet<string> physicsModels )
	{
		var bipeds = cache.Type is CacheType.Shared or CacheType.Multiplayer;
		foreach ( var tag in cache.Tags )
		{
			if ( (tag.Class == CrateClass || (bipeds && tag.Class == BipedClass)) && tag.Cache == cache && ObjectTag.IsObject( tag )
				&& new ObjectTag( tag ).Model is { Address: not 0 } physicsModel )
				physicsModels.Add( ResourcePath( physicsModel.Path ) );

			if ( tag.Class != ModelTag.Class || string.IsNullOrEmpty( tag.Path ) || tag.Cache != cache || tag.Address == 0 )
				continue;

			var path = ResourcePath( tag.Path );
			var variants = ModelTag.VariantCount( tag );
			if ( !models.TryGetValue( path, out var found ) || variants > found.Variants )
			{
				models[path] = (cache.FilePath, tag.Id, variants);
				var renderModel = new ModelTag( tag ).RenderModel;
				var slots = renderModel is { Address: not 0 } && renderModel.Class == RenderModelTag.Class ? ShaderFunctions.Slots( new RenderModelTag( renderModel ).Materials ) : [];
				if ( slots.Count > 0 )
					functions[path] = slots;
				else
					functions.Remove( path );
			}
		}
	}

	/// <summary>
	/// The resource path of an object model (an <c>hlmt</c>'s path).
	/// </summary>
	internal static string ModelPath( string tagPath ) => $"mount://halo2/{ResourcePath( tagPath )}.vmdl";

	static readonly global::Blam.TagClass CrateClass = new( "bloc" );
	static readonly global::Blam.TagClass BipedClass = new( "bipd" );

	// A crate's or biped's model with Halo's physics (PhysicsModelBodies), by its model's resource name, and its resource path.
	internal static string PhysicsModelName( string modelResource ) => $"{modelResource}_physics";

	internal static string PhysicsModelPath( string tagPath ) => $"mount://halo2/{PhysicsModelName( ResourcePath( tagPath ) )}.vmdl";

	/// <summary>
	/// A tag path as a resource path.
	/// </summary>
	internal static string ResourcePath( string tagPath ) => tagPath.Replace( '\\', '/' );

	/// <summary>
	/// The resource name of one bitmap in a bitmap tag; the ones after the first get an index suffix (as in the CE mount).
	/// </summary>
	internal static string TextureName( string tagPath, int index ) =>
		index == 0 ? ResourcePath( tagPath ) : $"{ResourcePath( tagPath )}_{index}";

	// The resource name of a frame of a bitmap's sequence, <tag path>/<sequence>_<frame>, as in the Halo CE mount.
	// A sprite sequence's frame is its sprite, cropped to its own texture; another's is its bitmap (first bitmap + frame).
	internal static string FrameTextureName( string tagPath, int sequence, int frame ) => $"{ResourcePath( tagPath )}/{sequence}_{frame}";

	/// <summary>
	/// The cache file of a map by name (lockout), or null if there's none.
	/// </summary>
	internal CacheFile GetMap( string name )
	{
		var path = System.IO.Path.Combine( GameDirectory, "h2_maps_win64_dx11", $"{name}.map" );
		return File.Exists( path ) ? GetCache( path ) : null;
	}

	/// <summary>
	/// An opened cache file, kept open for loaders. Multiplayer maps open with shared.map.
	/// </summary>
	internal CacheFile GetCache( string mapPath )
	{
		Lazy<CacheFile> cache;
		lock ( _caches )
		{
			if ( !_caches.TryGetValue( mapPath, out cache ) )
			{
				var isShared = string.Equals( mapPath, _sharedPath, StringComparison.OrdinalIgnoreCase );
				_caches[mapPath] = cache = new Lazy<CacheFile>( () => CacheFile.Open( mapPath, isShared ? null : GetCache( _sharedPath ) ) );
			}
		}

		return cache.Value;
	}

	protected override void Shutdown()
	{
		MccBrightness.Stop();
		SceneUpkeep.StopWatching();
		Halo2ParticleWorld.StopWatching();
		Halo2ParticleLibrary.Clear();
		Textures?.Dispose();
		Textures = null;
		Sounds?.Dispose();
		Sounds = null;

		lock ( _caches )
		{
			foreach ( var cache in _caches.Values.Where( c => c.IsValueCreated ) )
				cache.Value.Dispose();
			_caches.Clear();
		}
	}
}
