namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// A copy of the frame as far as it's drawn, grabbed by an object of its own among the transparent things: where
/// <see cref="SceneUpkeep"/> sorts the object decides what the copy holds. Shaders that work on what's behind them
/// read it in place of s&amp;box's copy for translucent readers, which is taken after the opaque things only and masked
/// by the readers' own depth (black outside their silhouette, and everywhere for a reader that writes no depth).
/// </summary>
class FrameGrab
{
	readonly string _name;
	readonly string _frameAttribute;
	readonly string _hasFrameAttribute;

	SceneCustomObject _object;
	Texture _frame;
	bool _copyFailed;

	// One copy per frame size, kept: views of different sizes (a game view and the editor's) grab in turn every frame, and a
	// copy replaced and collected while draws recorded with it wait on the GPU is read freed, which faults the GPU.
	readonly Dictionary<(ImageFormat Format, Vector2 Size), Texture> _frames = new();

	/// <param name="name">The copy's name (for its render target and the warning if copies fail).</param>
	/// <param name="frameAttribute">The renderer attribute the copy goes in.</param>
	/// <param name="hasFrameAttribute">The renderer attribute set while there's a copy.</param>
	public FrameGrab( string name, string frameAttribute, string hasFrameAttribute )
	{
		_name = name;
		_frameAttribute = frameAttribute;
		_hasFrameAttribute = hasFrameAttribute;
	}

	/// <summary>
	/// The copy of the last view's frame (null before the first one, or when copies fail).
	/// </summary>
	public Texture Frame => _copyFailed ? null : _frame;

	/// <summary>
	/// The object grabbing the frame in a scene's world (made on first use).
	/// </summary>
	public SceneCustomObject ObjectIn( SceneWorld world )
	{
		if ( _object is { } existing && existing.IsValid() && existing.World == world )
			return existing;

		_object?.Delete();
		_object = new SceneCustomObject( world ) { RenderOverride = _ => Grab() };
		_object.Flags.IsOpaque = false;
		_object.Flags.IsTranslucent = true;
		_object.Flags.CastShadows = false;
		return _object;
	}

	public void Delete()
	{
		_object?.Delete();
		_object = null;
		foreach ( var frame in _frames.Values )
			frame.Dispose();
		_frames.Clear();
		_frame = null;
	}

	void Grab()
	{
		if ( _copyFailed )
			return;

		try
		{
			var frame = Graphics.GrabFrameTexture( _frameAttribute ).ColorTarget;
			var key = (frame.ImageFormat, frame.Size);
			if ( !_frames.TryGetValue( key, out _frame ) )
				_frames[key] = _frame = Texture.CreateRenderTarget( _name, frame.ImageFormat, frame.Size );
			Graphics.CopyTexture( frame, _frame );
		}
		catch ( Exception e )
		{
			_copyFailed = true;
			Halo2Mount.Current?.Logger.Warning( $"Couldn't copy the frame ({_name}): {e.Message}" );
		}
	}

	/// <summary>
	/// Hands the copy to a renderer (again whenever it was made anew for another size).
	/// </summary>
	public void Apply( ModelRenderer renderer )
	{
		if ( Frame is { } frame )
		{
			renderer.Attributes.Set( _frameAttribute, frame );
			renderer.Attributes.Set( _hasFrameAttribute, true );
		}
		else
		{
			renderer.Attributes.Set( _hasFrameAttribute, false );
		}
	}
}
