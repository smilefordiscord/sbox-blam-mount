using System.Runtime.CompilerServices;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Hides renderers for the mount's culling. Each kind of culling hides for its own reason, and a
/// renderer draws again once no reason hides it. Only a renderer hidden here is shown again here, so
/// a game hiding one itself (a first-person body) keeps it hidden.
/// </summary>
static class RenderCulling
{
	[Flags]
	public enum Reason
	{
		/// <summary>Not drawn by Halo's visibility: in no cluster the camera's cluster can see.</summary>
		Visibility = 1,

		/// <summary>Smaller on screen than the model's lowest level of detail.</summary>
		Size = 2,

		// Another structure BSP than the one the camera is in (see OneBspShown).
		OtherBsp = 4,
	}

	sealed class State
	{
		public Reason Reasons;

		// Whether rendering was turned off here.
		public bool Disabled;
	}

	static readonly ConditionalWeakTable<SceneObject, State> States = new();

	/// <summary>
	/// Reasons stopped culling a renderer: it, the reasons that stopped, and those still culling it
	/// (none: it's drawn in the camera's view again). Main thread.
	/// </summary>
	public static event Action<ModelRenderer, Reason, Reason> Cleared;

	public static void Set( ModelRenderer renderer, Reason reason, bool on ) => Set( renderer, reason, on ? reason : 0 );

	/// <summary>Sets which of the reasons in <paramref name="reasons"/> hide the renderer.</summary>
	public static void Set( ModelRenderer renderer, Reason reasons, Reason on )
	{
		if ( renderer.SceneObject is not { } so || !so.IsValid() )
			return;

		var state = States.GetOrCreateValue( so );
		var was = state.Reasons;
		state.Reasons = (was & ~reasons) | (on & reasons);

		// The renderer sets these itself whenever it updates its scene object, so they're set again
		// each time, and given back as the renderer would have them. Each is changed only if it
		// differs: a change costs the scene object an update.
		var hidden = state.Reasons != 0;
		if ( hidden || state.Disabled )
			SetRendering( so, !hidden );
		state.Disabled = hidden;

		if ( (was & ~state.Reasons) != 0 )
			Cleared?.Invoke( renderer, was & ~state.Reasons, state.Reasons );
	}

	static void SetRendering( SceneObject so, bool enabled )
	{
		if ( so.RenderingEnabled != enabled )
			so.RenderingEnabled = enabled;
	}

	/// <summary>Whether this reason culls the renderer.</summary>
	public static bool IsCulledBy( ModelRenderer renderer, Reason reason ) =>
		renderer.SceneObject is { } so && States.TryGetValue( so, out var state ) && (state.Reasons & reason) != 0;

	/// <summary>Whether the renderer draws nothing, its shadow included.</summary>
	public static bool IsCulled( ModelRenderer renderer ) =>
		renderer.SceneObject is { } so && States.TryGetValue( so, out var state ) && state.Reasons != 0;
}
