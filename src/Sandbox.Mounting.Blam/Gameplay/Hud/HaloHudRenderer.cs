using System;
using Sandbox.Rendering;

namespace Halo.UI;

// Blends the HUD in gamma space as Halo does; a linear-light blend loses faint adds and over-darkens.
// Quads render into a layer after post processing, then one full screen pass composites it.
// Meters, scope ladders and the sensor sweep are computed per texel on the GPU, so the CPU only writes vertices.
// If a shader fails to load, Submit returns false and the HUD draws nothing.
[Title( "Halo HUD Renderer" ), Icon( "layers" )]
public sealed class HaloHudRenderer : Component {
	const string QuadShader = "shaders/halo_hud.shader";
	const string CompositeShader = "shaders/halo_hud_composite.shader";

	static Material _quad, _composite;
	static float _loadedAt = float.MinValue;
	static bool _warned;

	static bool LoadMaterials() {
		if ( _quad is not null && _composite is not null ) return true;
		if ( RealTime.Now - _loadedAt < 10 ) return false;
		_loadedAt = RealTime.Now;

		_quad ??= Load( QuadShader );
		_composite ??= Load( CompositeShader );
		if ( _quad is not null && _composite is not null ) return true;

		if ( !_warned ) {
			_warned = true;
			Log.Warning( $"Halo HUD: {(_quad is null ? QuadShader : CompositeShader)} isn't available; the HUD can't draw." );
		}
		return false;
	}

	static Material Load( string path ) {
		try {
			var shader = Shader.Load( path );
			if ( shader is null || !shader.IsValid || shader.IsError ) return null;
			var material = Material.FromShader( shader );
			return material is not null && material.IsValid && !material.IsError ? material : null;
		}
		catch ( Exception ) {
			return null;
		}
	}

	// A null texture draws a solid rectangle; rotation is degrees clockwise.
	public readonly record struct Quad( Texture Texture, Rect Rect, Color Color, bool Additive, float Rotation ) {
		// Reads the bitmap's alpha as 1, for the additive half of Halo's HUD blend.
		public bool Opaque { get; init; }

		// A texel sub-rectangle such as a glyph, sampled from the top mip and clamped half a texel inside.
		public Rect? Region { get; init; }

		// Texels the shader computes instead of reading the bitmap.
		public QuadEffect Effect { get; init; }
	}

	// Matches the shader's HaloHudMode.
	public enum QuadKind {
		Bitmap = 0,
		// The raw meter bitmap recolored by its thresholds, HudMeter's rule.
		Meter = 1,
		// Two raw maps combined, such as the sniper's pitch ladders.
		Multitexture = 2,
		// The motion sensor sweep under its mask.
		Sweep = 3,
	}

	// Laid out as the HUD shader reads them.
	public struct QuadEffect {
		public QuadKind Kind;
		public Texture Secondary;
		public Vector4 A, B, C, D, E;
	}

	// Rotates three vertex buffers so the one being filled isn't one the GPU may still be reading.
	const int BufferCount = 3;
	readonly GpuBuffer<Vertex>[] _buffers = new GpuBuffer<Vertex>[BufferCount];
	int _bufferIndex;
	int _capacity;
	Vertex[] _vertices = Array.Empty<Vertex>();
	readonly Dictionary<Texture, RenderAttributes> _attributes = new();

	CommandList _commands;
	CameraComponent _camera;
	bool _recorded;
	float _submitted = -1;

	// Draws quads in order this frame.
	// False, drawing nothing, when the shaders aren't available.
	public bool Submit( CameraComponent camera, IReadOnlyList<Quad> quads ) {
		using var perf = Halo.HaloPerf.Measure( "HUD.Renderer.Submit" );
		if ( camera is null || !LoadMaterials() ) {
			Clear();
			return false;
		}

		Attach( camera );
		_commands.Reset();
		_recorded = true;
		_submitted = Time.Now;
		if ( quads.Count == 0 ) return true;

		var screen = Screen.Size;
		if ( screen.x <= 0 || screen.y <= 0 ) return true;

		var count = quads.Count * 6;
		var buffer = Buffer( count );
		for ( var i = 0; i < quads.Count; i++ )
			WriteQuad( i * 6, quads[i], screen );
		buffer.SetData( _vertices.AsSpan( 0, count ), 0 );

		var layer = _commands.GetRenderTarget( "HaloHudLayer", ImageFormat.RGBA16161616F, 1, 1 );
		_commands.Clear( layer, Color.Transparent );
		_commands.SetRenderTarget( layer );

		// One draw per run of bitmap quads sharing a texture, and one per generated quad.
		_effectSet ^= 1;
		var effects = _effectAttributes[_effectSet] ??= new List<RenderAttributes>();
		var effectCount = 0;
		for ( var start = 0; start < quads.Count; ) {
			var quad = quads[start];
			var texture = Source( quad );
			var end = start + 1;

			if ( quad.Effect.Kind != QuadKind.Bitmap ) {
				if ( effectCount == effects.Count ) effects.Add( new RenderAttributes() );
				var attributes = effects[effectCount++];
				SetEffect( attributes, texture, quad.Effect );
				_commands.Draw( buffer, _quad, start * 6, 6, attributes, Graphics.PrimitiveType.Triangles );
				start = end;
				continue;
			}

			while ( end < quads.Count && quads[end].Effect.Kind == QuadKind.Bitmap && Source( quads[end] ) == texture )
				end++;

			_commands.Draw( buffer, _quad, start * 6, (end - start) * 6, Attributes( texture ), Graphics.PrimitiveType.Triangles );
			start = end;
		}

		_commands.ClearRenderTarget();
		_commands.Attributes.GrabFrameTexture( "ColorBuffer" );
		_commands.Attributes.Set( "HaloHudLayer", layer.ColorTexture );
		_commands.Blit( _composite );
		_commands.ReleaseRenderTarget( layer );
		return true;
	}

	// Mount bitmaps use their mipmapped RGBA copies; the HUD's own textures are used as they are.
	// A solid rectangle samples white.
	static Texture Source( in Quad quad ) {
		var texture = quad.Texture;
		if ( texture is null ) return Texture.White;
		// Generated quads read raw copies; regions read their page as it is.
		if ( quad.Effect.Kind != QuadKind.Bitmap || quad.Region is not null ) return texture;
		return string.IsNullOrEmpty( texture.ResourcePath ) ? texture : HudTextures.Mipped( texture );
	}

	RenderAttributes Attributes( Texture texture ) {
		if ( _attributes.TryGetValue( texture, out var attributes ) ) return attributes;
		if ( _attributes.Count > 512 ) _attributes.Clear();
		attributes = new RenderAttributes();
		attributes.Set( "HaloHudTexture", texture );
		attributes.Set( "HaloHudTexture2", Texture.White );
		attributes.Set( "HaloHudMode", 0f );
		_attributes[texture] = attributes;
		return attributes;
	}

	// One attribute set per generated quad, double-buffered across frames so the GPU isn't reading one being rewritten.
	readonly List<RenderAttributes>[] _effectAttributes = new List<RenderAttributes>[2];
	int _effectSet;

	static void SetEffect( RenderAttributes attributes, Texture texture, in QuadEffect effect ) {
		attributes.Set( "HaloHudTexture", texture );
		attributes.Set( "HaloHudTexture2", effect.Secondary ?? Texture.White );
		attributes.Set( "HaloHudMode", (float)(int)effect.Kind );
		attributes.Set( "HaloHudA", effect.A );
		attributes.Set( "HaloHudB", effect.B );
		attributes.Set( "HaloHudC", effect.C );
		attributes.Set( "HaloHudD", effect.D );
		attributes.Set( "HaloHudE", effect.E );
	}

	GpuBuffer<Vertex> Buffer( int count ) {
		if ( count > _capacity ) {
			_capacity = Math.Max( count, Math.Max( 256, _capacity * 2 ) );
			for ( var i = 0; i < BufferCount; i++ ) {
				_buffers[i]?.Dispose();
				_buffers[i] = null;
			}
			_vertices = new Vertex[_capacity];
		}

		_bufferIndex = (_bufferIndex + 1) % BufferCount;
		return _buffers[_bufferIndex] ??= new GpuBuffer<Vertex>( _capacity, GpuBuffer.UsageFlags.Vertex, "Halo HUD quads" );
	}

	static readonly Vector2[] Corners = { new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 0 ), new( 1, 1 ), new( 0, 1 ) };

	// Two clip-space triangles, rotated about the rect's center.
	void WriteQuad( int at, in Quad quad, Vector2 screen ) {
		var rect = quad.Rect;
		var center = rect.Center;
		var radians = quad.Rotation.DegreeToRadian();
		var cos = MathF.Cos( radians );
		var sin = MathF.Sin( radians );
		var color = new Vector4( quad.Color.r, quad.Color.g, quad.Color.b, quad.Color.a );
		var kind = new Vector4( quad.Additive ? 1 : 0, quad.Opaque ? 1 : 0, quad.Region is not null ? 1 : 0, 0 );

		// A region's uv, clamped to its outermost texel centers.
		Vector2 uv0 = Vector2.Zero, uv1 = Vector2.One, lo = Vector2.Zero, hi = Vector2.One;
		if ( quad.Region is { } region && quad.Texture is { Width: > 0, Height: > 0 } page ) {
			var size = new Vector2( page.Width, page.Height );
			uv0 = new Vector2( region.Left, region.Top ) / size;
			uv1 = new Vector2( region.Right, region.Bottom ) / size;
			lo = new Vector2( region.Left + 0.5f, region.Top + 0.5f ) / size;
			hi = new Vector2( region.Right - 0.5f, region.Bottom - 0.5f ) / size;
		}

		for ( var i = 0; i < 6; i++ ) {
			var corner = Corners[i];
			var dx = (corner.x - 0.5f) * rect.Width;
			var dy = (corner.y - 0.5f) * rect.Height;
			var x = center.x + dx * cos - dy * sin;
			var y = center.y + dx * sin + dy * cos;

			_vertices[at + i] = new Vertex {
				Position = new Vector3( x / screen.x * 2 - 1, 1 - y / screen.y * 2, 0.5f ),
				TexCoord0 = new Vector4( uv0.x + (uv1.x - uv0.x) * corner.x, uv0.y + (uv1.y - uv0.y) * corner.y, lo.x, lo.y ),
				TexCoord1 = color,
				Normal = new Vector3( hi.x, hi.y, 0 ),
				Tangent = kind,
			};
		}
	}

	public void Clear() {
		if ( !_recorded ) return;
		_commands?.Reset();
		_recorded = false;
	}

	// A frame the HUD didn't submit, such as while dead, draws nothing.
	protected override void OnPreRender() {
		if ( _recorded && _submitted != Time.Now )
			Clear();
	}

	void Attach( CameraComponent camera ) {
		_commands ??= new CommandList( "Halo HUD" );
		if ( _camera == camera ) return;

		Detach();
		_camera = camera;
		// After post processing, and after the scope screen effect at -10000.
		_camera.AddCommandList( _commands, Stage.AfterPostProcess, 10000 );
	}

	void Detach() {
		if ( _camera.IsValid() && _commands is not null )
			_camera.RemoveCommandList( _commands );
		_camera = null;
	}

	protected override void OnDisabled() {
		Clear();
		Detach();
	}

	protected override void OnDestroy() {
		Clear();
		Detach();
		for ( var i = 0; i < BufferCount; i++ ) {
			_buffers[i]?.Dispose();
			_buffers[i] = null;
		}
	}
}
