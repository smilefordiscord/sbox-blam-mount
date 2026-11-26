namespace Sandbox.Mounting.Blam.Halo3;

// The sun of a Halo 3 scene: a directional light whose only job is its shadow map, which halo3_shader reads for the objects' shadows (on the level and on each other, themselves included).
// MCC draws each object's shadow from its own dominant light direction (the lightmap's, where the object stands) into a 512 pixel map and darkens the frame inside the object's bounds (rasterizer\shaders\shadow_apply); here one sun stands for them all, as the CE mount's does (Halo1/ScenarioSceneLoader.BuildLighting).
static class Halo3SunLight
{
	public const string ObjectName = "sun";

	public static void Build( Halo3ObjectLighting lighting )
	{
		if ( lighting?.Sun is not { } sun )
			return;

		var toLight = sun.Direction;
		// Shadows fall at least 45 degrees below the horizontal, as the CE mount keeps them: a low sun (none on the multiplayer maps seen) would otherwise throw them across the level.
		var across = new Vector2( -toLight.x, -toLight.y );
		var length = across.Length;
		var travel = length < 0.707f
			? new Vector3( across.x, across.y, -MathF.Sqrt( 1 - length * length ) )
			: new Vector3( across.x * 0.707f / length, across.y * 0.707f / length, -0.707f );

		var go = new GameObject( true, ObjectName );
		go.Tags.Add( "halo3_sun" );
		go.WorldRotation = Rotation.LookAt( travel );

		var directional = go.AddComponent<DirectionalLight>();
		// The colour only lights what isn't drawn by halo3_shader (which lights from the lightmaps and probes): a game's own objects placed in the level.
		var color = sun.Intensity / MathF.Max( 1, MathF.Max( sun.Intensity.x, MathF.Max( sun.Intensity.y, sun.Intensity.z ) ) );
		directional.LightColor = new Color( color.x, color.y, color.z );
		directional.Shadows = true;
		// As the CE mount: the default split (0.91) gives the last cascade everything from about 20 world units out, where a vehicle's shadow lost half its darkness.
		directional.ShadowCascadeSplitRatio = 0.85f;
		// Screen-space contact shadows trace the depth buffer, level included, whose shadows are baked.
		directional.ContactShadows = false;
	}
}
