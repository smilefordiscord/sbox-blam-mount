namespace Blam.Halo1;

/// <summary>
/// Tag group codes for lights, lens flares, weather, particle effects, lightning, contrails and glows.
/// </summary>
public static class EffectTagClasses
{
	public static readonly TagClass Light = new( "ligh" );
	public static readonly TagClass LensFlare = new( "lens" );
	public static readonly TagClass WeatherParticleSystem = new( "rain" );
	public static readonly TagClass Effect = new( "effe" );
	public static readonly TagClass Particle = new( "part" );
	public static readonly TagClass ParticleSystem = new( "pctl" );
	public static readonly TagClass PointPhysics = new( "pphy" );
	public static readonly TagClass Lightning = new( "elec" );
	public static readonly TagClass Contrail = new( "cont" );
	public static readonly TagClass Glow = new( "glw!" );
}
