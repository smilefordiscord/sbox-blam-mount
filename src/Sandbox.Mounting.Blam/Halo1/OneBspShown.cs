using Sandbox.Internal;
using System.Runtime.CompilerServices;

namespace Sandbox.Mounting.Blam.Halo1;

// Draws one structure BSP at a time, as Halo loads one: campaign levels repeat the rooms around a BSP switch in both BSPs, and with both drawn (the editor, a game that shows the whole level) the copies fight.
// Only while more than one BSP is enabled, so a game switching BSPs itself is left alone. The camera switches BSPs where Halo does, walking into a BSP switch trigger volume with its source BSP drawn (see BspSwitches), else when it's no longer in the drawn BSP but in another; with it outside every BSP all are drawn. Collision stays for all of them.
sealed class OneBspShown : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	const float RefreshSeconds = 1;

	// Each BSP's index and the renderers drawing it.
	readonly Dictionary<int, List<ModelRenderer>> _renderers = new();
	readonly Dictionary<int, BspDrawing> _drawings = new();
	readonly HashSet<ModelRenderer> _hidden = new();
	readonly List<(GameObject Volume, int Source, int Destination)> _switches = new();
	GameObject _switchedBy;
	RealTimeSince _refreshed;
	SceneCustomObject _probe;
	Vector3? _view;
	int _shown = -1;

	protected override void OnEnabled()
	{
		_refreshed = RefreshSeconds;

		// Every view draws this, so the editor's viewport is followed as well as a game's camera.
		_probe = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = _ => _view = Graphics.CameraPosition };
		_probe.Flags.IsOpaque = false;
		_probe.Flags.IsTranslucent = true;
		_probe.Flags.CastShadows = false;
		_probe.Bounds = new BBox( new Vector3( -1e7f ), new Vector3( 1e7f ) );
		SourceVisibility.KeepDrawn( _probe );
	}

	protected override void OnDisabled()
	{
		_probe?.Delete();
		_probe = null;
		Show( -1, again: true );
		Shown.Remove( Scene );
	}

	// IPreRenderSubscriber is what gets OnPreRender called for a mount component (see MirrorReflections).
	protected override void OnPreRender()
	{
		if ( _refreshed >= RefreshSeconds )
		{
			_refreshed = 0;
			Refresh();
		}

		// Only while the level shows more than one BSP.
		var enabled = _renderers.Where( b => b.Value.Any( r => r.IsValid() && r.GameObject.Active ) ).Select( b => b.Key ).ToList();
		if ( enabled.Count < 2 )
		{
			Show( -1 );
			return;
		}

		var camera = Game.IsPlaying && Scene.Camera is { } main && main.IsValid() && main.Enabled ? main.WorldPosition : _view;
		if ( camera is not { } eye )
			return;

		var halo = new System.Numerics.Vector3( eye.x, eye.y, eye.z ) / HaloCeMount.WorldScale;
		bool Inside( int bsp ) => _drawings.TryGetValue( bsp, out var drawing ) && drawing.View.FindCluster( halo ) >= 0;

		var shown = _shown >= 0 && enabled.Contains( _shown ) && Inside( _shown ) ? _shown : enabled.Where( Inside ).DefaultIfEmpty( -1 ).First();
		// Once a volume switched, none switches again until the camera leaves it (a level's volumes each way can overlap).
		if ( _switchedBy is { } last && (!last.IsValid() || !InVolume( last, eye )) )
			_switchedBy = null;
		foreach ( var (volume, source, destination) in _switches )
		{
			if ( _switchedBy is null && source == shown && enabled.Contains( destination ) && volume.IsValid() && InVolume( volume, eye ) )
			{
				shown = destination;
				_switchedBy = volume;
				break;
			}
		}
		Show( shown );
	}

	void Show( int bsp, bool again = false )
	{
		if ( bsp == _shown && !again )
			return;
		_shown = bsp;
		Shown.AddOrUpdate( Scene, bsp );

		foreach ( var renderer in _hidden )
		{
			if ( renderer.IsValid() )
				RenderCulling.Set( renderer, RenderCulling.Reason.OtherBsp, false );
		}
		_hidden.Clear();
		if ( bsp < 0 )
			return;

		foreach ( var (index, renderers) in _renderers )
		{
			if ( index == bsp )
				continue;
			foreach ( var renderer in renderers )
			{
				if ( !renderer.IsValid() )
					continue;
				RenderCulling.Set( renderer, RenderCulling.Reason.OtherBsp, true );
				_hidden.Add( renderer );
			}
		}
	}

	// Finds each BSP's renderers by their bsp_<index> tag, and its visibility by its cluster models.
	void Refresh()
	{
		_renderers.Clear();
		foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
		{
			if ( !renderer.IsValid() || BspOf( renderer.GameObject ) is not { } bsp )
				continue;
			if ( !_renderers.TryGetValue( bsp, out var list ) )
				_renderers[bsp] = list = new();
			list.Add( renderer );
			if ( renderer.Model is { } model && ClusterVisibility.DrawingOf( model ) is { } drawing )
				_drawings[bsp] = drawing;
		}

		_switches.Clear();
		foreach ( var volume in Scene.FindAllWithTag( SwitchTag ) )
		{
			var parts = volume.Name.Split( ' ' );
			if ( parts.Length == 4 && int.TryParse( parts[2], out var source ) && int.TryParse( parts[3], out var destination ) )
				_switches.Add( (volume, source, destination) );
		}

		// Renderers found since are hidden too.
		Show( _shown, again: true );
	}

	// A switch volume is a box from its object's origin to its scale along its axes.
	static bool InVolume( GameObject volume, Vector3 point )
	{
		var local = volume.WorldTransform.WithScale( 1 ).PointToLocal( point );
		var size = volume.WorldScale;
		return local.x >= 0 && local.x <= size.x && local.y >= 0 && local.y <= size.y && local.z >= 0 && local.z <= size.z;
	}

	public const string SwitchTag = "halo_bsp_switch";

	static int? BspOf( GameObject go )
	{
		foreach ( var tag in go.Tags.TryGetAll() )
		{
			if ( tag.StartsWith( "bsp_" ) && int.TryParse( tag.AsSpan( 4 ), out var index ) )
				return index;
		}
		return null;
	}

	// The BSP each scene draws alone, -1 for all; the world visibility loader follows it.
	public static readonly ConditionalWeakTable<Scene, object> Shown = new();

	public static int ShownIn( Scene scene ) => Shown.TryGetValue( scene, out var bsp ) && bsp is int index ? index : -1;
}
