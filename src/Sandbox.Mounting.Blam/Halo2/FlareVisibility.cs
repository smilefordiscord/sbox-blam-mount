using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// The lens flares' visibility, kept from frame to frame: one slot per flare of every lens flare model in a scene
/// (<see cref="LensFlareModels"/>), measured against the depth buffer after the opaque things and eased toward each
/// frame's value by halo2_flare_visibility.shader; halo2_lens_flare.shader reads it (HaloFlareVisibility at the
/// renderer's HaloFlareSlot plus the flare's index in its model, in its view's row).
/// </summary>
/// <remarks>
/// Halo tests each flare with an occlusion query and moves a visibility byte toward the result every frame it draws:
/// 1/8 of the way in, half of it out (scratch research notes, overnight/effects/lens_flare_re.md). Measured afresh
/// each frame, a flare stepped on and off as an edge crossed it, where MCC's fades.
/// </remarks>
sealed class FlareVisibility
{
	public const string ShaderPath = "shaders/halo2_flare_visibility.shader";
	public const string SlotAttribute = "HaloFlareSlot";
	public const string TextureAttribute = "HaloFlareVisibility";
	public const string FrameAttribute = "HaloFlareFrame";
	public const string HasFrameAttribute = "HaloHasFlareFrame";

	// The shaders' Slots and Rows: a row of results per view rendering the scene, past the slots three columns naming
	// the view that wrote it.
	const int Slots = 1024;
	const int Rows = 4;
	const int TagColumns = 3;
	const int ScanInterval = 10;

	// How far ahead of the camera the flares in view and their frame grab sort, in near plane distances: nearest of all
	// the transparent things, the flares nearer still.
	const float FlareSortDistance = 2;
	const float GrabSortDistance = 3;

	static Material _material;

	// What the flares add to: halo2_lens_flare.shader adds in gamma space, so it needs the frame behind it, and s&box's
	// copy for translucent readers is black for a reader that writes no depth.
	readonly FrameGrab _behind = new( "halo2 lens flare frame", FrameAttribute, HasFrameAttribute );

	Texture _params;
	Texture _current;
	Texture _previous;
	CommandList _commands;
	int _frame;

	readonly float[] _data = new float[Slots * 3 * 4];
	readonly Dictionary<ModelRenderer, int> _slots = new();
	readonly List<(ModelRenderer Renderer, IReadOnlyList<LensFlareModels.Flare> Flares, bool FirstPerson)> _renderers = new();
	int _nextSlot;

	/// <summary>
	/// The command list to run after the opaque things (null if the shader can't be loaded).
	/// </summary>
	public CommandList Commands => _commands ??= Build();

	CommandList Build()
	{
		_material ??= Material.Create( "halo2/flare_visibility.vmat", ShaderPath );
		if ( _material is null )
			return null;

		_params = Texture.Create( Slots, 3, ImageFormat.RGBA32323232F ).WithData( new ReadOnlySpan<float>( _data ) ).Finish();
		_current = FloatTarget( "halo2 flare visibility" );
		_previous = FloatTarget( "halo2 flare visibility last" );

		var commands = new CommandList( "Halo 2 lens flare visibility" );
		commands.Attributes.Set( "HaloFlareParams", _params );
		Pass( commands, 0, _current, _previous );
		Pass( commands, 1, _previous, _current );
		commands.ClearRenderTarget();
		return commands;
	}

	// Built rather than Texture.CreateRenderTarget( name, format, size ), which now swaps RGBA32323232F for RGBA8888.
	static Texture FloatTarget( string name ) =>
		Texture.CreateRenderTarget().WithFormat( ImageFormat.RGBA32323232F ).WithSize( Slots + TagColumns, Rows ).WithInitialColor( Color.Magenta ).Create( name );

	static void Pass( CommandList commands, int pass, Texture source, Texture target )
	{
		commands.ResourceBarrierTransition( target, ResourceState.RenderTarget, 0 );
		commands.SetRenderTarget( RenderTarget.From( target, null ) );
		commands.Attributes.Set( "HaloFlareSource", source );
		commands.Attributes.Set( "HaloFlarePass", pass );
		commands.Blit( _material );
		commands.ResourceBarrierTransition( target, ResourceState.PixelShaderResource, 0 );
	}

	/// <summary>
	/// Hands every lens flare renderer its slot and writes the slots' flares (where they are now) for this frame.
	/// </summary>
	public void Update( Scene scene )
	{
		if ( Commands is null )
			return;

		if ( _frame++ % ScanInterval == 0 )
			Scan( scene );

		Array.Clear( _data );
		var scale = Halo2Mount.WorldScale;
		foreach ( var (renderer, flares, firstPerson) in _renderers )
		{
			if ( !renderer.IsValid() || !_slots.TryGetValue( renderer, out var slot ) )
				continue;

			renderer.Attributes.Set( SlotAttribute, (float)slot );
			renderer.Attributes.Set( TextureAttribute, _current );
			_behind.Apply( renderer );
			if ( !renderer.Enabled || !renderer.GameObject.Active )
				continue;

			var transform = renderer.WorldTransform;
			for ( var j = 0; j < flares.Count && slot + j < Slots; j++ )
			{
				var flare = flares[j];
				var lens = flare.Lens;
				var sun = flare.Sun || lens.IsSun;
				var kind = (lens.Flags & 2) != 0 ? 3 : sun ? 2 : 1;
				var position = sun ? flare.Position : transform.PointToWorld( flare.Position );
				var forward = transform.Rotation * flare.Forward;
				Write( slot + j, 0, new Vector4( position, kind ) );
				Write( slot + j, 1, new Vector4( forward, lens.OcclusionRadius * scale ) );
				Write( slot + j, 2, new Vector4( Math.Clamp( lens.OcclusionOffsetDirection, 0, 2 ), (lens.Flags & 0x10) != 0 ? 1 : 0, (lens.Flags & 0x20) != 0 ? 1 : 0, firstPerson ? 1 : 0 ) );
			}
		}

		_params.Update( new ReadOnlySpan<float>( _data ), 0, 0, Slots, 3 );
	}

	/// <summary>
	/// Sorts the lens flares in view after every other transparent thing, right after a grab of the frame they add to:
	/// Halo draws them in a pass of their own after the transparent things, before the post-processing (scratch research
	/// notes, overnight/effects/lens_flare_re.md). Each renderer in view gets bounds just ahead of the camera
	/// (<see cref="InFront"/>), the grab a little farther; out of view they're as they are.
	/// </summary>
	/// <remarks>
	/// The flares read s&amp;box's copy for translucent readers before, which is black for them (it's masked by the
	/// reader's depth, and a flare writes none), so each added its colour to black in linear space: on Zanzibar's hinge
	/// light about 0.9x MCC's glow near its middle and 0.6x at its edge, and more over the lamp's dark rim and pole than
	/// over the wall, where MCC's is the bitmap x the tag's colour at every radius.
	/// </remarks>
	public void KeepLast( SceneWorld world, Vector3 eye, Vector3 forward, float zNear, Frustum frustum )
	{
		if ( Commands is null )
			return;

		SourceVisibility.Kept( _behind.ObjectIn( world ) ).Bounds = InFront( eye, forward, zNear * GrabSortDistance );
		foreach ( var (renderer, _, _) in _renderers )
		{
			if ( !renderer.IsValid() || renderer.SceneObject is not { } sceneObject || renderer.Model is not { } model )
				continue;

			var bounds = model.Bounds.Transform( renderer.WorldTransform );
			SourceVisibility.Kept( sceneObject ).Bounds = frustum.IsInside( bounds, true ) ? InFront( eye, forward, zNear * FlareSortDistance ) : bounds;
		}
	}

	/// <summary>
	/// How far <see cref="InFront"/>'s slab reaches every other way (s&amp;box units): past any map.
	/// </summary>
	const float SlabReach = 1_000_000;

	/// <summary>
	/// Bounds that sort <paramref name="distance"/> from the camera and stay in its view however it moves: a slab on its
	/// forward side of the world axis it faces most, its near face that far from the camera, reaching
	/// <see cref="SlabReach"/> every other way. s&amp;box sorts by the distance to the nearest point of the bounds, which
	/// is the near face.
	/// </summary>
	/// <remarks>
	/// The camera that renders isn't always where it was in OnPreRender: the editor moves its viewport's camera after the
	/// scene's tick, and a game's camera effects shift it at render time. A small box two near plane distances ahead
	/// fell behind the near plane as soon as the camera had moved that far, and the flares in view went out with it
	/// whenever the camera moved. The slab only sorts nearer (down to 0, inside it) when the camera has moved past it.
	/// </remarks>
	static BBox InFront( Vector3 eye, Vector3 forward, float distance )
	{
		var x = MathF.Abs( forward.x );
		var y = MathF.Abs( forward.y );
		var z = MathF.Abs( forward.z );
		var axis = x >= y && x >= z ? Vector3.Forward : y >= z ? Vector3.Left : Vector3.Up;
		if ( Vector3.Dot( forward, axis ) < 0 )
			axis = -axis;

		var across = Vector3.One - axis.Abs();
		var near = eye + axis * distance;
		var far = eye + axis * (distance + SlabReach);
		return BBox.FromPoints( [near - across * SlabReach, far + across * SlabReach] );
	}

	void Write( int slot, int row, Vector4 value )
	{
		var at = (row * Slots + slot) * 4;
		_data[at] = value.x;
		_data[at + 1] = value.y;
		_data[at + 2] = value.z;
		_data[at + 3] = value.w;
	}

	/// <summary>
	/// Finds the scene's lens flare renderers; each keeps its slots while it lives (a slot's visibility is its history).
	/// </summary>
	void Scan( Scene scene )
	{
		_renderers.Clear();
		var seen = new HashSet<ModelRenderer>();
		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
		{
			if ( LensFlareModels.FlaresOf( renderer.Model ) is not { Count: > 0 } flares )
				continue;

			if ( !_slots.ContainsKey( renderer ) )
			{
				if ( _nextSlot + flares.Count > Slots )
					Compact( seen );
				if ( _nextSlot + flares.Count > Slots )
					continue;

				_slots[renderer] = _nextSlot;
				_nextSlot += flares.Count;
			}

			seen.Add( renderer );
			_renderers.Add( (renderer, flares, OnViewModel( renderer )) );
		}

		foreach ( var gone in _slots.Keys.Where( r => !seen.Contains( r ) && !r.IsValid() ).ToList() )
			_slots.Remove( gone );
	}

	// Whether a flare rides a renderer drawn in the overlay layer: a first person view model, whose own flares Halo 2
	// tests in its occlusion pass before it draws that weapon, so the weapon never hides them.
	static bool OnViewModel( ModelRenderer renderer )
	{
		for ( var go = renderer.GameObject.Parent; go.IsValid(); go = go.Parent )
			if ( go.GetComponent<ModelRenderer>() is { } holder && holder.RenderOptions.Overlay )
				return true;

		return false;
	}

	/// <summary>
	/// Hands out the slots again from 0 when they've run out (renderers come and go): their histories restart.
	/// </summary>
	void Compact( HashSet<ModelRenderer> keep )
	{
		_slots.Clear();
		_nextSlot = 0;
		foreach ( var renderer in keep )
		{
			var count = LensFlareModels.FlaresOf( renderer.Model )?.Count ?? 0;
			_slots[renderer] = _nextSlot;
			_nextSlot += count;
		}
	}

	public void Delete()
	{
		_behind.Delete();
		_params?.Dispose();
		_current?.Dispose();
		_previous?.Dispose();
	}
}
