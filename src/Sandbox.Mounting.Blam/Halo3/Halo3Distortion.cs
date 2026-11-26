namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's screen distortion (heat shimmer, refraction rings): the distortion particles' quads for this frame, which Halo3PostFx draws into its offset target (halo3_distortion.shader) and its motion blur pass applies, as MCC's particles add offsets into a half-res target that displacement_motion_blur reads.
// The particle runtime writes the quads on the main thread after its snapshot (Halo3ParticleWorld.WriteDistortion); the command list draws them indirectly, so its count changes without rebuilding it.
sealed class Halo3Distortion
{
	public const string ShaderPath = "shaders/halo3_distortion.shader";
	public const int MaximumQuads = 4096;

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, Halo3Distortion> Scenes = new();

	public static Halo3Distortion For( Scene scene ) => scene is not null && Scenes.TryGetValue( scene, out var distortion ) ? distortion : null;

	public static void Register( Scene scene, Halo3Distortion distortion ) => Scenes.AddOrUpdate( scene, distortion );

	readonly Vertex[] _staging = new Vertex[MaximumQuads * 6];
	readonly uint[] _arguments = new uint[4];

	public GpuBuffer<Vertex> Vertices { get; } = new( MaximumQuads * 6, GpuBuffer.UsageFlags.Vertex, "halo3 distortion quads" );
	public GpuBuffer<uint> Arguments { get; } = new( 4, GpuBuffer.UsageFlags.IndirectDrawArguments, "halo3 distortion draw" );

	// Whether last frame drew any (MCC applies the offsets only then).
	public bool Any { get; private set; }

	static Material _material;
	public static Material Material => _material ??= Material.Create( "halo3/distortion.vmat", ShaderPath );

	// Where this frame's quads go: six vertices (two triangles) each.
	public Vertex[] Begin() => _staging;

	public void End( int quads )
	{
		quads = Math.Clamp( quads, 0, MaximumQuads );
		if ( quads > 0 )
			Vertices.SetData( new ReadOnlySpan<Vertex>( _staging, 0, quads * 6 ) );
		_arguments[0] = (uint)(quads * 6);
		_arguments[1] = quads > 0 ? 1u : 0u;
		Arguments.SetData( _arguments );
		Any = quads > 0;
	}

	public void Delete()
	{
		Vertices.Dispose();
		Arguments.Dispose();
	}
}
