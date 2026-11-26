using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// Every biped model with a human-like skeleton as a player model: players/<model>.vmdl refitted onto s&box's human skeleton, and players/<model>.clothing wearing it.
static class PlayerModels
{
	public const string Folder = "players";

	// Names for the models players know by another name (the multiplayer Spartan wears the Chief's); the rest are titled after their tag.
	static readonly Dictionary<string, string> Titles = new( StringComparer.OrdinalIgnoreCase )
	{
		["cyborg"] = "Master Chief",
		["floodcombat_human"] = "Flood Combat Form",
		["floodcombat elite"] = "Flood Combat Elite",
	};

	public static string ModelName( string biped ) => $"{Folder}/{biped}";
	public static string ClothingName( string biped ) => $"{Folder}/{biped}.clothing";

	public static string Title( string biped ) => Titles.TryGetValue( biped, out var title )
		? title
		: string.Join( ' ', biped.Split( ['_', ' '], StringSplitOptions.RemoveEmptyEntries ).Select( w => char.ToUpperInvariant( w[0] ) + w[1..] ) );

	public static void Register( MountContext context, string ident, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class.ToString() != "bipd" || string.IsNullOrEmpty( tag.Path ) || ObjectTags.GetObjectModel( cache, tag ) is not { } model || model.Class != TagClasses.GbxModel )
				continue;

			// One per model: bipeds that share one (armed and unarmed marines, cinematic poses) would only repeat it.
			var biped = System.IO.Path.GetFileName( HaloCeMount.ResourcePath( model.Path ) );
			if ( registered.Contains( ModelName( biped ) ) )
				continue;

			try
			{
				if ( !PlayerBody.CanFit( new GbxModelTag( cache, model ).Nodes.Select( n => n.Name ) ) )
					continue;
			}
			catch
			{
				continue;
			}

			registered.Add( ModelName( biped ) );
			context.Add( ResourceType.Model, ModelName( biped ), new PlayerModelLoader( cache.FilePath, model.Id ) );
			context.Add( ResourceType.GameResource, ClothingName( biped ), new PlayerClothingLoader( Title( biped ), $"mount://{ident}/{ModelName( biped )}.vmdl" ) );
		}
	}
}

// A biped's model refitted as a player model, built on the main thread like the rest.
sealed class PlayerModelLoader( string mapPath, uint tagId ) : HaloCeLoader
{
	protected override Task<object> LoadAsync()
	{
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult( Load() );

		return LoadOnMainThread();
	}

	async Task<object> LoadOnMainThread()
	{
		await GameTask.MainThread();
		return Load();
	}

	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		return tag is null ? null : GbxModelBuilder.BuildPlayer( Host, Path, cache, new GbxModelTag( cache, tag ) );
	}
}

// The player model as something to wear: it replaces the body and takes no other clothing.
sealed class PlayerClothingLoader( string title, string model ) : HaloCeLoader
{
	protected override object Load() => new Clothing
	{
		Title = $"{title} (Halo CE)",
		Type = Clothing.ClothingType.PlayerModel,
		BodyModel = model,
		AllowedSlots = Clothing.Slots.None,
	};
}
