namespace Sandbox.Mounting.Blam;

/// <summary>
/// Base for a game inside Halo: The Master Chief Collection. MCC is a single Steam app, and each
/// game lives in its own folder inside it, so each game is its own mount.
/// </summary>
public abstract class MccMount : BaseGameMount
{
	public const long MccAppId = 976730;

	public override long? SteamAppId => MccAppId;

	/// <summary>
	/// The game's folder inside the MCC install, e.g. <c>halo1</c>.
	/// </summary>
	protected abstract string GameFolder { get; }

	/// <summary>
	/// Full path to <see cref="GameFolder"/>, set when MCC and this game are installed.
	/// </summary>
	protected string GameDirectory { get; private set; }

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( MccAppId ) )
			return;

		var mccDirectory = InstallPath( context.GetFileSystem( MccAppId ) );
		if ( string.IsNullOrEmpty( mccDirectory ) )
			return;

		// MCC lets each game be uninstalled separately.
		var gameDirectory = System.IO.Path.Combine( mccDirectory, GameFolder );
		if ( !System.IO.Directory.Exists( gameDirectory ) )
			return;

		GameDirectory = gameDirectory;
		IsInstalled = true;
	}

	// Mounts get their game as a read-only file system without its path, but the map readers open its files on disk, so the path is read from the folder under it.
	static string InstallPath( BaseFileSystem files )
	{
		if ( files is null )
			return null;

		var path = files.GetFullPath( "/" );
		if ( !string.IsNullOrEmpty( path ) )
			return path;

		var inner = typeof( BaseFileSystem ).GetField( "system", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )?.GetValue( files ) as Zio.IFileSystem;
		return inner?.ConvertPathToInternal( Zio.UPath.Root );
	}

	// Leaves only these models (resource names) in spawn menus, which list every model that isn't DeveloperOnly; the variants games use (camouflaged, first person, in parts) stay in the asset browser.
	protected void ShowInSpawnMenus( IEnumerable<string> models )
	{
		var shown = models.Select( x => $"mount://{Ident}/{x.Replace( '\\', '/' ).Trim( '/' )}" ).Select( x => x.EndsWith( ".vmdl" ) ? x : $"{x}.vmdl" ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		// Resources register with the mount the engine knows, the dev loader's when this build runs under it.
		var registered = Directory.Get( Ident ) ?? this;
		foreach ( var model in registered.GetAll( ResourceType.Model ) )
		{
			if ( !shown.Contains( model.Path ) )
				model.Flags |= ResourceFlags.DeveloperOnly;
		}
	}
}
