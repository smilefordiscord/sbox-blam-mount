using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds a level's weather (snow on b40 and c40, rain and bugs in c10's swamp) as GPU particles
/// that follow the camera, kept to the places it falls by a <see cref="WeatherVolume"/> mask.
/// </summary>
static class Weather
{
	/// <summary>
	/// Most particles of one type around the camera.
	/// </summary>
	const int MaxParticles = 6000;

	/// <summary>
	/// Textures are kept under this size on each side.
	/// </summary>
	const int MaxTextureSize = 16384;

	public static Model Build( HaloCeMount host, string name, CacheFile cache, IReadOnlyList<StructureBspEffects> bsps )
	{
		var volumes = WeatherVolume.Build( bsps );
		if ( volumes.Count == 0 )
			return null;

		var builder = Model.Builder.WithName( name );
		var meshes = 0;
		var types = new Dictionary<uint, WeatherParticleSystemTag>();

		for ( var v = 0; v < volumes.Count; v++ )
		{
			var volume = volumes[v];
			var (mask, columns) = MaskTexture( volume );
			var present = volume.Cells.Where( c => c != 0 ).Distinct().ToList();

			foreach ( var value in present )
			{
				var system = volume.Systems[value - 1];
				if ( !types.TryGetValue( system.ParticleSystem.Id, out var rain ) )
					types[system.ParticleSystem.Id] = rain = new WeatherParticleSystemTag( cache, system.ParticleSystem );

				var simulated = WeatherMotion.SimulatedCounts( rain.Types );
				for ( var t = 0; t < rain.Types.Count; t++ )
				{
					var type = rain.Types[t];
					var count = WeatherMotion.DrawnCount( type, simulated[t], MaxParticles );
					if ( type.SpriteBitmap is null || count == 0 )
						continue;

					var material = ParticleMaterial.Create( host, $"{name}/{v}_{value}_{t}.vmat", cache, type.SpriteBitmap, 0, type.BlendFunction, type.RenderMode, weather: true );
					if ( material is null )
						continue;

					var cube = WeatherMotion.CubeSize( type );
					var fade = type.FadeDistance * HaloCeMount.WorldScale;
					material.Set( "g_flAnimationRate", (type.AnimationRate.X + type.AnimationRate.Y) * 0.5f );
					material.Set( "g_vWeatherVelocity", (Vector3)WeatherMotion.Velocity( cache, type, system ) * HaloCeMount.WorldScale );
					material.Set( "g_flWeatherCube", cube * HaloCeMount.WorldScale );

					// Wandering particles (bugs) swing about by roughly how far their acceleration
					// would carry them in a second.
					material.Set( "g_flWander", (type.AccelerationMagnitude.X + type.AccelerationMagnitude.Y) * 0.5f * 0.15f * HaloCeMount.WorldScale );
					material.Set( "g_vWeatherFade", new Vector4( fade.X, fade.Y, fade.Z, fade.W ) );
					material.Set( "g_flFadeMode", (float)type.FadeMode );
					material.Set( "g_tWeatherMask", mask );
					material.Set( "g_vMaskOrigin", (Vector3)volume.Origin * HaloCeMount.WorldScale );
					material.Set( "g_flMaskCell", volume.CellSize * HaloCeMount.WorldScale );
					material.Set( "g_vMaskSize", new Vector3( volume.SizeX, volume.SizeY, volume.SizeZ ) );
					material.Set( "g_flMaskColumns", (float)columns );
					material.Set( "g_flMaskValue", (float)value );

					var vertices = new List<ParticleVertex>();
					var indices = new List<int>();
					var random = new Random( HashCode.Combine( v, value, t ) );

					// 0x4: random rotation.
					var rotate = (type.Flags & 0x4) != 0;
					for ( var i = 0; i < count; i++ )
					{
						var color = System.Numerics.Vector4.Lerp( type.ColorLower, type.ColorUpper, random.NextSingle() );
						ParticleVertex.AddQuad( vertices, indices, new ParticleVertex
						{
							Position = new Vector3( random.NextSingle(), random.NextSingle(), random.NextSingle() ),
							Corner = new Vector4( 0, 0, random.NextSingle(), random.NextSingle() ),
							Color = new Vector4( color.X, color.Y, color.Z, color.W ),
							Particle = new Vector4( float.Lerp( type.Radius.X, type.Radius.Y, random.NextSingle() ) * HaloCeMount.WorldScale, 0, 0, 0 ),
							Motion = new Vector4( 0, 0, 0, rotate ? random.NextSingle() * MathF.PI * 2 : 0 ),
						} );
					}

					// Drawn wherever the camera is near the volume.
					var min = (Vector3)volume.Origin * HaloCeMount.WorldScale;
					var size = new Vector3( volume.SizeX, volume.SizeY, volume.SizeZ ) * volume.CellSize * HaloCeMount.WorldScale;
					var bounds = new BBox( min, min + size ).Grow( cube * HaloCeMount.WorldScale );

					builder.AddMesh( ParticleMaterial.CreateMesh( material, vertices, indices, bounds ) );
					meshes++;
				}
			}
		}

		return meshes == 0 ? null : builder.Create();
	}

	/// <summary>
	/// The volume's cells as a texture: one slice of the grid per z, slices side by side in rows.
	/// </summary>
	static (Texture Texture, int Columns) MaskTexture( WeatherVolume volume )
	{
		var columns = Math.Clamp( MaxTextureSize / volume.SizeX, 1, volume.SizeZ );
		var rows = (volume.SizeZ + columns - 1) / columns;
		var width = volume.SizeX * columns;
		var height = volume.SizeY * rows;

		// A float texture, so the cell values come back exact (an 8 bit texture might be read as sRGB).
		var pixels = new float[width * height];
		for ( var z = 0; z < volume.SizeZ; z++ )
		{
			var tileX = z % columns * volume.SizeX;
			var tileY = z / columns * volume.SizeY;
			for ( var y = 0; y < volume.SizeY; y++ )
			{
				for ( var x = 0; x < volume.SizeX; x++ )
					pixels[(tileY + y) * width + tileX + x] = volume.Cells[x + volume.SizeX * (y + volume.SizeY * z)];
			}
		}

		var texture = Texture.Create( width, height, ImageFormat.R32F )
			.WithData( new ReadOnlySpan<float>( pixels ) )
			.WithStaticUsage()
			.Finish();
		return (texture, columns);
	}
}
