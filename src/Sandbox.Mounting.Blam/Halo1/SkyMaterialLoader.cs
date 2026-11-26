using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Makes a SkyBox2D material from a Halo CE sky tag.
/// </summary>
/// <remarks>
/// Halo draws the sky model centered on the camera with depth testing off, so it's infinitely
/// far away and its layers stack in draw order, whatever their size: a dome tens of thousands of
/// units across, with water, distant land and clouds only a few thousand across inside it.
/// Placing the model in the level can't reproduce that, so it's rendered from its center into
/// six faces against the level's fog color, and drawn as the skybox with halo1_sky.shader.
///
/// The sky gets its own copies of its textures (<see cref="MaterialTextures.PrivateCopies"/>) and
/// renders with texture streaming off. With the shared textures, as the first thing to use them,
/// it often got them half loaded: b30's ring came out as a faint ghost of itself (its alpha
/// averaged out) and its clouds not at all, differently from session to session and more often in
/// an editor that was still starting up.
///
/// The layers draw in Halo's order (region order: b30's 00.sky, 14.threshold, ..., 31.clouds,
/// 40.fog band). s&amp;box sorts translucent objects by the distance from the camera to their bounds
/// (farthest first) and doesn't promise to keep a model's meshes in order, and with every layer
/// around the camera they tied, broken by render world slots, which other objects being removed
/// reshuffle: b30's dome came out over the ring and clouds on some loads. So each face gets its
/// own copy of every layer, as its own object, bounded by a box straight ahead of that face's
/// camera that starts nearer for each later layer. Their distances then put them in order.
/// </remarks>
sealed class SkyMaterialLoader( string mapPath, uint tagId ) : HaloCeLoader
{
	public const string ShaderPath = "shaders/halo1_sky.shader";
	/// <summary>
	/// Pixels along each face's side. At 2048 a face's 90 degrees get about as many pixels per
	/// degree as a 1080p screen, so tiled layers (b30's stars repeat 8 times, its clouds 6) stay as
	/// sharp as Halo draws them. 512 was visibly blurry.
	/// </summary>
	const int FaceSize = 2048;

	/// <summary>
	/// (forward, up) of each face, matching the table in halo1_sky.shader.
	/// </summary>
	static readonly (Vector3 Forward, Vector3 Up)[] Faces =
	[
		(Vector3.Forward, Vector3.Up),
		(Vector3.Backward, Vector3.Up),
		(Vector3.Left, Vector3.Up),
		(Vector3.Right, Vector3.Up),
		(Vector3.Up, Vector3.Backward),
		(Vector3.Down, Vector3.Forward),
	];

	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( tag is null )
			return null;

		var sky = new SkyTag( cache, tag );
		var material = Material.Create( Path, ShaderPath );
		if ( material is null )
			return null;

		// Start with the fog color. Halo clears the screen to it and draws the sky model over
		// that, so a sky without a model (c10's swamp, c20, chillout, b40/c40's snow) is just it.
		var fog = (Vector3)sky.OutdoorFogColor;
		material.Set( "g_tSkyAtlas", Texture.Create( 1, 1 )
			.WithData( new[] { ToByte( fog.x ), ToByte( fog.y ), ToByte( fog.z ), (byte)255 } )
			.WithStaticUsage()
			.Finish() );

		if ( sky.Model is null || sky.Model.Class != TagClasses.GbxModel )
			return material;

		var gbxModel = new GbxModelTag( cache, sky.Model );
		var regions = Enumerable.Range( 0, gbxModel.Regions.Count ).ToList();
		var layers = new List<Model>[Faces.Length];
		// The sky's own copies of its textures (see RenderAtlas).
		using ( MaterialTextures.PrivateCopies( cache ) )
		{
			for ( var face = 0; face < Faces.Length; face++ )
			{
				layers[face] = new List<Model>();
				for ( var i = 0; i < regions.Count; i++ )
				{
					var layer = GbxModelBuilder.Build( Host, $"{Path}/sky_{face}_{regions[i]}.vmdl", cache, gbxModel, scale: 1, collision: false,
						region: regions[i], meshBounds: LayerBounds( Faces[face].Forward, regions.Count - i ) );
					if ( layer is not null )
						layers[face].Add( layer );
				}
			}
		}

		if ( layers[0].Count == 0 )
			return material;

		// This loads while a map's scene is being built, and rendering another scene in the
		// middle of that confuses the scene being built. Render the sky once the build is done.
		_ = RenderLater( material, layers, sky );
		return material;
	}

	/// <summary>
	/// Bounds straight ahead of a face's camera, starting <paramref name="rank"/> thousand units
	/// out and covering everything it sees beyond that (see above).
	/// </summary>
	static BBox LayerBounds( Vector3 forward, int rank )
	{
		const float far = 10_000_000;
		var near = rank * 1000f;
		var mins = new Vector3( -far );
		var maxs = new Vector3( far );
		for ( var axis = 0; axis < 3; axis++ )
		{
			if ( forward[axis] > 0.5f )
				mins[axis] = near;
			else if ( forward[axis] < -0.5f )
				maxs[axis] = -near;
		}

		return new BBox( mins, maxs );
	}

	async Task RenderLater( Material material, List<Model>[] layers, SkyTag sky )
	{
		// Wait for the next frame, by which time the scene build that loaded this has finished.
		await GameTask.Yield();

		// A game loading the map's scene file can still be deserializing it then (resource loads
		// pump the main thread queue), with a callback batch open. Objects created inside one
		// are woken when the batch ends, under the loading scene instead of the sky's own scene.
		for ( var frame = 0; frame < 600 && IsCallbackBatchOpen(); frame++ )
			await GameTask.Yield();

		try
		{
			material.Set( "g_tSkyAtlas", await RenderAtlas( layers, sky ) );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"Couldn't render the sky for {Path}: {e.Message}" );
		}
	}

	static readonly System.Reflection.PropertyInfo CurrentCallbackBatch = typeof( Scene ).Assembly
		.GetType( "Sandbox.CallbackBatch" )?
		.GetProperty( "Current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public );

	/// <summary>
	/// Whether the engine is collecting GameObject callbacks into a batch (e.g. while a scene file
	/// is being deserialized). There's no public way to ask, so this peeks at the engine's
	/// internal state, and says no if that ever changes.
	/// </summary>
	static bool IsCallbackBatchOpen()
	{
		try { return CurrentCallbackBatch?.GetValue( null ) is not null; }
		catch { return false; }
	}

	/// <summary>
	/// Renders the six faces into <paramref name="atlas"/>, returning each face's average color.
	/// </summary>
	static Vector3[] RenderFaces( Scene[] scenes, CameraComponent[] cameras, Bitmap bitmap, byte[] atlas )
	{
		var averages = new Vector3[Faces.Length];
		for ( var face = 0; face < Faces.Length; face++ )
		{
			var camera = cameras[face];
			using ( scenes[face].Push() )
				Sandbox.Rendering.TextureStreaming.ExecuteWithDisabled( () => camera.RenderToBitmap( bitmap, false ) );

			var pixels = bitmap.GetPixels();
			var sum = Vector3.Zero;
			for ( var y = 0; y < FaceSize; y++ )
			for ( var x = 0; x < FaceSize; x++ )
			{
				var color = pixels[y * FaceSize + x];
				var i = ((y * FaceSize * 6) + face * FaceSize + x) * 4;
				atlas[i] = ToByte( color.r );
				atlas[i + 1] = ToByte( color.g );
				atlas[i + 2] = ToByte( color.b );
				atlas[i + 3] = 255;
				sum += new Vector3( color.r, color.g, color.b );
			}

			averages[face] = sum / (FaceSize * FaceSize);
		}

		return averages;
	}

	static byte ToByte( float value ) => (byte)(Math.Clamp( value, 0, 1 ) * 255);

	/// <summary>
	/// Renders the sky model from its center into six square faces, side by side.
	/// </summary>
	/// <remarks>
	/// The first render is the first use of the sky's textures, and even with streaming off it can
	/// come out before their full mips are in (the face rendered first got a ghost of b30's ring).
	/// So it renders again a few frames later, until no face's average color changes any more
	/// (the clouds scroll, so the pixels themselves never settle).
	/// </remarks>
	static async Task<Texture> RenderAtlas( List<Model>[] layers, SkyTag sky )
	{
		var atlas = new byte[FaceSize * 6 * FaceSize * 4];
		var fog = (Vector3)sky.OutdoorFogColor;

		// A scene per face, with that face's copy of the layers (see above).
		var scenes = new Scene[Faces.Length];
		var cameras = new CameraComponent[Faces.Length];
		try
		{
			for ( var face = 0; face < Faces.Length; face++ )
			{
				scenes[face] = new Scene();
				using ( scenes[face].Push() )
				{
					foreach ( var layer in layers[face] )
						new GameObject( true, "sky layer" ).AddComponent<ModelRenderer>().Model = layer;

					var camera = new GameObject( true, "camera" ).AddComponent<CameraComponent>();
					camera.BackgroundColor = new Color( fog.x, fog.y, fog.z );
					camera.FieldOfView = 90;
					camera.ZNear = 1;
					camera.ZFar = 100_000_000;
					camera.WorldRotation = Rotation.LookAt( Faces[face].Forward, Faces[face].Up );
					cameras[face] = camera;
				}
			}

			using var bitmap = new Bitmap( FaceSize, FaceSize );
			Vector3[] previous = null;
			for ( var pass = 0; pass < 8; pass++ )
			{
				if ( pass > 0 )
				{
					for ( var frame = 0; frame < 10; frame++ )
						await GameTask.Yield();
				}

				var averages = RenderFaces( scenes, cameras, bitmap, atlas );

				if ( previous is not null && averages.Zip( previous ).All( p => (p.First - p.Second).Length < 0.002f ) )
					break;
				previous = averages;
			}
		}
		finally
		{
			foreach ( var scene in scenes )
				scene?.Destroy();
		}

		return Texture.Create( FaceSize * 6, FaceSize )
			.WithData( atlas )
			.WithStaticUsage()
			.Finish();
	}
}
