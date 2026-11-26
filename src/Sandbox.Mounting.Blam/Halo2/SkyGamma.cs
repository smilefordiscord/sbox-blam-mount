namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's sky drawn in Halo's gamma space: two objects that <see cref="SceneUpkeep"/> sorts around the sky's layers,
/// one taking the frame to gamma space just before the first layer, one taking it back just after the last
/// (halo2_sky_gamma.shader). In between, the layers write gamma space colours (halo2_transparent.shader, while their
/// renderers have <see cref="LayerAttribute"/> set), so their ordinary blending is Halo's.
/// </summary>
/// <remarks>
/// <para>Halo blends every layer in its gamma space frame buffer. Blending in s&amp;box's linear frame instead changed
/// how a partly transparent layer mixes with what's behind it: Lockout's grey mountain clouds brightened the dark
/// rock too much and barely darkened the snow (a solid wisp, where MCC's are a thin veil), and premultiplied soft
/// edges darkened by their coverage^2.2 (a dark band where the mountains fade out over the floor below them). The
/// layers also add to one another, which a gamma space add against s&amp;box's frame copy (taken once, before them)
/// couldn't do.</para>
/// <para>The objects are tagged as sky layers so the open water's reflection cube, which renders the sky's layers
/// alone, converts its faces the same way.</para>
/// </remarks>
sealed class SkyGamma
{
	public const string ShaderPath = "shaders/halo2_sky_gamma.shader";

	/// <summary>
	/// The renderer attribute that makes a sky layer write gamma space colours (1) rather than linear ones.
	/// </summary>
	public const string LayerAttribute = "HaloSkyGamma";

	static Material _material;

	SceneCustomObject _toGamma, _toLinear;
	bool _failed;

	/// <summary>
	/// Whether the conversion runs: false once a frame copy failed, when the layers must write linear colours again.
	/// </summary>
	public bool Works => !_failed;

	/// <summary>
	/// The objects in a scene's world (made on first use): the one taking the frame to gamma space, and back.
	/// </summary>
	public (SceneCustomObject ToGamma, SceneCustomObject ToLinear) ObjectsIn( SceneWorld world )
	{
		if ( _toGamma is { } a && a.IsValid() && a.World == world && _toLinear is { } b && b.IsValid() && b.World == world )
			return (a, b);

		// On the main thread (rendering isn't).
		_material ??= Material.Create( "halo2/sky_gamma.vmat", ShaderPath );

		Delete();
		_toGamma = Make( world, toLinear: false );
		_toLinear = Make( world, toLinear: true );
		return (_toGamma, _toLinear);
	}

	SceneCustomObject Make( SceneWorld world, bool toLinear )
	{
		var sceneObject = new SceneCustomObject( world ) { RenderOverride = _ => Convert( toLinear ) };
		sceneObject.Flags.IsOpaque = false;
		sceneObject.Flags.IsTranslucent = true;
		sceneObject.Flags.CastShadows = false;
		sceneObject.Tags.Add( SceneUpkeep.SkyLayerTag );
		return sceneObject;
	}

	public void Delete()
	{
		_toGamma?.Delete();
		_toLinear?.Delete();
		_toGamma = _toLinear = null;
	}

	void Convert( bool toLinear )
	{
		if ( _failed || _material is null )
			return;

		try
		{
			var a = Graphics.Attributes;
			Graphics.GrabFrameTexture( "HaloSkyGammaFrame" );
			a.Set( "HaloSkyGammaToLinear", toLinear ? 1f : 0f );
			Graphics.Blit( _material, a );
		}
		catch ( Exception e )
		{
			// The layers go back to linear colours (see Works); a frame already half converted is lost.
			_failed = true;
			Halo2Mount.Current?.Logger.Warning( $"Couldn't draw the Halo 2 sky in gamma space: {e.Message}" );
		}
	}
}
