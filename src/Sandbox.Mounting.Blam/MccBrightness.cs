using System.Reflection;
using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam;

/// <summary>
/// MCC's gamma slider (Settings, Video, Gamma: 1-10), as the <c>halo_gamma</c> console variable: a brightness step laid
/// over the finished frame of every Halo scene, Halo 1's and Halo 2's alike.
/// </summary>
/// <remarks>
/// <para>MCC applies the slider itself, over everything a game draws, the HUD included (scratch research notes,
/// gamma.md: the games' own gamma passes ignore it). Measured live on Halo 2's Lockout from the same view at 1, 6 and
/// 10: an offset of about 0.0622 per step in its gamma space frame, clamped, with 6 changing nothing (flat colours
/// reach the screen at 0.97 times their value there, which the mount's colours already match). So at 6, the default,
/// nothing is drawn; otherwise a command list (halo_brightness.shader) runs on the camera of each scene with Halo
/// content, last before s&amp;box's post-processing (where Halo 2's bloom runs too, and the frame is the linear one the
/// Halo shaders write; after the post-processing it isn't, and the offset came out wrong). Like the bloom, only in views
/// with post-processing. s&amp;box's UI draws later, so unlike MCC's HUD it isn't brightened.</para>
/// </remarks>
static class MccBrightness
{
	public const string ShaderPath = "shaders/halo_brightness.shader";

	/// <summary>
	/// MCC's default slider position, where the frame is left as it is.
	/// </summary>
	const float Neutral = 6;

	/// <summary>
	/// The brightness each slider step adds to the gamma space frame.
	/// </summary>
	const float StepOffset = 0.0622f;

	/// <summary>
	/// Where the pass runs among the camera's command lists before s&amp;box's post-processing: after Halo 2's bloom
	/// (order 0), as MCC brightens the frame after the game's bloom.
	/// </summary>
	const int Order = 1000;

	[ConVar( "halo_gamma", Help = "MCC's gamma slider (1-10, 6 = MCC's default, unchanged): brightens or darkens Halo scenes as MCC does.", Saved = true )]
	public static float Gamma { get; set; } = Neutral;

	static readonly Sandbox.Diagnostics.Logger Log = new( "blam" );

	static readonly object Lock = new();
	static System.Threading.Timer _timer;
	static int _users, _queued;
	static Material _material;

	/// <summary>
	/// The command list on each scene's camera, and the slider value it was made for.
	/// </summary>
	sealed class Applied
	{
		public CameraComponent Camera;
		public CommandList Commands;
		public float Gamma;
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, Applied> Scenes = new();

	/// <summary>
	/// The process-wide slot naming the copy of this assembly that owns the setting: the editor can have more than one
	/// copy loaded (the Halo 1 mount's and the Halo 2 development loader's), and each would otherwise register the
	/// console variable and draw the pass.
	/// </summary>
	const string OwnerSlot = "Sandbox.Mounting.Blam.MccBrightness.Owner";

	static bool _owner;

	/// <summary>
	/// Starts (once for any number of mounts, in one copy of the assembly) registering the console variable and watching
	/// scenes twice a second.
	/// </summary>
	public static void Start()
	{
		lock ( Lock )
		{
			if ( _users++ > 0 )
				return;

			var assembly = typeof( MccBrightness ).Assembly;
			if ( AppDomain.CurrentDomain.GetData( OwnerSlot ) is System.Reflection.Assembly other && other != assembly )
				return;

			AppDomain.CurrentDomain.SetData( OwnerSlot, assembly );
			_owner = true;
			RegisterConsoleVariable();
			_timer = new System.Threading.Timer( _ => Queue(), null, 0, 500 );
		}
	}

	public static void Stop()
	{
		lock ( Lock )
		{
			if ( --_users > 0 || !_owner )
				return;

			_owner = false;
			AppDomain.CurrentDomain.SetData( OwnerSlot, null );
			_timer?.Dispose();
			_timer = null;
			MainThread.Queue( RemoveAll );
		}
	}

	/// <summary>
	/// The mount isn't in s&amp;box's type library, so its console variable system doesn't find <see cref="Gamma"/> by
	/// itself: makes the console variable it would have made (a ManagedCommand with the engine's cookies, so the value
	/// is saved) and adds it. Both are internal, so by reflection.
	/// </summary>
	static void RegisterConsoleVariable()
	{
		// s&box registered it with the rest of the assembly's (a release mount, or the development loaders).
		if ( ConsoleSystem.GetValue( "halo_gamma" ) is not null )
			return;

		try
		{
			const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			var engine = typeof( ConsoleSystem ).Assembly;
			var property = typeof( MccBrightness ).GetProperty( nameof( Gamma ), any );
			var attribute = property.GetCustomAttribute<ConVarAttribute>();
			var cookies = engine.GetType( "Sandbox.Internal.GlobalGameNamespace" )?.GetProperty( "Cookie", any )?.GetValue( null );
			var constructor = engine.GetType( "Sandbox.ManagedCommand" )?.GetConstructors( any ).FirstOrDefault( c => c.GetParameters().Length == 4 );
			var add = engine.GetType( "Sandbox.ConVarSystem" )?.GetMethod( "AddConVar", any );
			if ( constructor is null || add is null )
			{
				Log.Warning( "Couldn't register halo_gamma: s&box's console variable internals changed" );
				return;
			}

			add.Invoke( null, [constructor.Invoke( [typeof( MccBrightness ).Assembly, property, attribute, cookies] )] );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Couldn't register halo_gamma: {e.InnerException?.Message ?? e.Message}" );
		}
	}

	static void Queue()
	{
		if ( System.Threading.Interlocked.Exchange( ref _queued, 1 ) == 1 )
			return;

		MainThread.Queue( Update );
	}

	/// <summary>
	/// The engine's set of every live scene, editor scenes included (<see cref="Scene.All"/> leaves those out).
	/// </summary>
	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	static IEnumerable<Scene> LiveScenes() =>
		AllScenesProperty?.GetValue( null ) is System.Collections.IEnumerable all ? all.OfType<Scene>().ToList() : Scene.All.ToList();

	static void Update()
	{
		_queued = 0;
		try
		{
			var gamma = Math.Clamp( Gamma, 1, 10 );
			foreach ( var scene in LiveScenes() )
			{
				if ( !scene.IsValid() )
					continue;

				Scenes.TryGetValue( scene, out var applied );
				var camera = Camera( scene );
				var wanted = MathF.Abs( gamma - Neutral ) > 0.001f && camera is not null && HasHaloContent( scene );
				if ( applied is not null && (!wanted || applied.Camera != camera || applied.Gamma != gamma) )
				{
					Remove( applied );
					Scenes.Remove( scene );
					applied = null;
				}

				if ( wanted && applied is null && Build( gamma ) is { } commands )
				{
					camera.AddCommandList( commands, Stage.BeforePostProcess, Order );
					Scenes.AddOrUpdate( scene, new Applied { Camera = camera, Commands = commands, Gamma = gamma } );
				}
			}
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"Couldn't apply halo_gamma: {e.Message}" );
		}
	}

	static void Remove( Applied applied )
	{
		if ( applied.Camera.IsValid() )
			applied.Camera.RemoveCommandList( applied.Commands, Stage.BeforePostProcess );
	}

	static void RemoveAll()
	{
		foreach ( var scene in LiveScenes() )
		{
			if ( Scenes.TryGetValue( scene, out var applied ) )
			{
				Remove( applied );
				Scenes.Remove( scene );
			}
		}
	}

	static CameraComponent Camera( Scene scene ) => scene.Camera is { } main && main.IsValid() && main.Enabled
		? main
		: scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );

	/// <summary>
	/// Whether a scene shows anything from the Halo mounts: a map scene, or a model of theirs.
	/// </summary>
	static bool HasHaloContent( Scene scene )
	{
		if ( scene.Source?.ResourcePath?.StartsWith( "mount://halo", StringComparison.OrdinalIgnoreCase ) == true )
			return true;

		return scene.GetAllComponents<ModelRenderer>()
			.Any( r => r.Model?.ResourcePath?.StartsWith( "mount://halo", StringComparison.OrdinalIgnoreCase ) == true );
	}

	static CommandList Build( float gamma )
	{
		_material ??= Material.Create( "blam/brightness.vmat", ShaderPath );
		if ( _material is null )
			return null;

		var commands = new CommandList( "MCC gamma" ) { Flags = CommandList.Flag.PostProcess };
		commands.Attributes.GrabFrameTexture( "HaloBrightnessFrame" );
		commands.Attributes.Set( "HaloBrightnessOffset", (gamma - Neutral) * StepOffset );
		commands.Blit( _material );
		return commands;
	}
}
