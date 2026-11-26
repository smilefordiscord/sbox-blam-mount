using System.Reflection;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Lets a game load a Halo CE map without freezing on it, for a loading screen: loading a map's
/// scene (<c>mount://haloce/maps/&lt;map&gt;.scene</c>) the usual way builds the whole level,
/// serializes it and deserializes it into the scene in one frame, which takes seconds on a big
/// level and leaves a loading screen frozen.
/// </summary>
/// <remarks>
/// <para><c>blam_scene_build &lt;scene path&gt;</c> starts building the level straight into the
/// active scene, and each <c>blam_scene_build_step</c> does one slice of it (about
/// <see cref="BuildSlice.Milliseconds"/> of work). The game runs a step a frame from its own frame
/// loop; the mount can't wait for a frame itself (awaiting goes back to the main thread's queue,
/// which drains within the same engine frame). <c>blam_scene_progress</c> goes from 0 to 1 and
/// <c>blam_scene_state</c> says where the build is: <c>building</c>, then <c>built</c> or
/// <c>failed</c>. The level's objects appear in the scene as the steps run, as the scene's own
/// (not networked) roots. Loading the scene as usual still works and builds it all at once.</para>
/// <code>
/// ConsoleSystem.Run( "blam_scene_build", "mount://haloce/maps/b30.scene" );
/// while ( ConsoleSystem.GetValue( "blam_scene_state" ) == "building" )
/// {
/// 	ConsoleSystem.Run( "blam_scene_build_step" );
/// 	await Task.Frame();
/// }
/// </code>
/// <para>The mount isn't in s&amp;box's type library, so the commands and the variables are added to
/// its console system by reflection, as <see cref="MccBrightness"/> adds halo_gamma.</para>
/// </remarks>
static class SceneBuilding
{
	static readonly Sandbox.Diagnostics.Logger Log = new( "blam" );

	/// <summary>
	/// How far the current <c>blam_scene_build</c> has got, 0 to 1. For a game to read. Not
	/// protected: the console system hides a protected variable from game code.
	/// </summary>
	[ConVar( "blam_scene_progress", Help = "How far the scene blam_scene_build is building has got, 0-1." )]
	public static float Progress { get; set; } = 1;

	/// <summary>
	/// Where the current <c>blam_scene_build</c> is: <c>building</c>, <c>built</c> or <c>failed</c>.
	/// </summary>
	[ConVar( "blam_scene_state", Help = "Where the scene blam_scene_build is building is: building, built or failed." )]
	public static string State { get; set; } = "";

	// The build the steps advance, the scene it builds into and the path it's for.
	static IEnumerator<float> _steps;
	static Scene _target;
	static string _path;

	[ConCmd( "blam_scene_build", Help = "Starts building a Halo CE map's scene (e.g. mount://haloce/maps/b30.scene) into the active scene; run blam_scene_build_step once a frame until blam_scene_state leaves building." )]
	static void Build( string path )
	{
		Stop();
		path = path?.Trim() ?? "";
		if ( Sandbox.Mounting.Directory.Get( "haloce" )?.GetByPath( path ) is not ScenarioSceneLoader loader )
		{
			Log.Warning( $"blam_scene_build: {path} isn't a Halo CE map scene" );
			State = "failed";
			return;
		}

		if ( Game.ActiveScene is not { } scene )
		{
			Log.Warning( $"blam_scene_build: no active scene to build {path} into" );
			State = "failed";
			return;
		}

		_path = path;
		_target = scene;
		_steps = loader.Build( scene );
		Progress = 0;
		State = "building";
	}

	[ConCmd( "blam_scene_build_step", Help = "Does one slice of the build blam_scene_build started (run it once a frame)." )]
	static void Step()
	{
		if ( _steps is null )
			return;

		try
		{
			if ( !_target.IsValid() )
				throw new InvalidOperationException( "the scene it was building into is gone" );
			if ( _steps.MoveNext() )
			{
				Progress = Math.Clamp( _steps.Current, 0, 0.99f );
				return;
			}
			State = "built";
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"blam_scene_build: couldn't build {_path}: {e.Message}" );
			State = "failed";
		}

		Stop();
		Progress = 1;
	}

	static void Stop()
	{
		_steps?.Dispose();
		_steps = null;
		_target = null;
	}

	/// <summary>
	/// Process-wide slot naming the copy of this assembly whose commands and variables are registered:
	/// the console system keeps the first of a name.
	/// </summary>
	const string OwnerSlot = "Sandbox.Mounting.Blam.SceneBuilding.Owner";

	/// <summary>
	/// Adds the commands and the variables to s&amp;box's console system, once per process.
	/// </summary>
	public static void Register()
	{
		lock ( Log )
		{
			if ( AppDomain.CurrentDomain.GetData( OwnerSlot ) is Assembly )
				return;
			AppDomain.CurrentDomain.SetData( OwnerSlot, typeof( SceneBuilding ).Assembly );
		}

		// s&box registered them with the rest of the assembly's (a release mount, or the development loaders).
		if ( ConsoleSystem.GetValue( "blam_scene_state" ) is not null )
			return;

		try
		{
			const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			var engine = typeof( ConsoleSystem ).Assembly;
			var cookies = engine.GetType( "Sandbox.Internal.GlobalGameNamespace" )?.GetProperty( "Cookie", any )?.GetValue( null );
			var constructor = engine.GetType( "Sandbox.ManagedCommand" )?.GetConstructors( any ).FirstOrDefault( c => c.GetParameters().Length == 4 );
			var system = engine.GetType( "Sandbox.ConVarSystem" );
			var addVariable = system?.GetMethod( "AddConVar", any );
			var addCommand = system?.GetMethod( "AddCommand", any );
			if ( constructor is null || addVariable is null || addCommand is null )
			{
				Log.Warning( "Couldn't register blam_scene_build: s&box's console internals changed" );
				return;
			}

			var assembly = typeof( SceneBuilding ).Assembly;
			foreach ( var name in new[] { nameof( Progress ), nameof( State ) } )
			{
				var property = typeof( SceneBuilding ).GetProperty( name, any );
				addVariable.Invoke( null, [constructor.Invoke( [assembly, property, property.GetCustomAttribute<ConVarAttribute>(), cookies] )] );
			}
			foreach ( var name in new[] { nameof( Build ), nameof( Step ) } )
			{
				var method = typeof( SceneBuilding ).GetMethod( name, any );
				addCommand.Invoke( null, [constructor.Invoke( [assembly, method, method.GetCustomAttribute<ConCmdAttribute>(), cookies] )] );
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"Couldn't register blam_scene_build: {e.InnerException?.Message ?? e.Message}" );
		}
	}
}
