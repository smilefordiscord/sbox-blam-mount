namespace Sandbox.Mounting.Blam.Halo3;

// How prefabs and scenes ask Halo3ParticleWorld to play a Halo 3 effect with engine components only, as Halo 2's (Halo2ParticleEffects on h2-particles): the root is tagged halo3_effect and the effect's token (plus halo3_fx_loop for one that loops for ever, as objects carry them) and keeps a TemporaryEffect so it's found the frame it spawns.
// Its "halo controls" child holds effect scales A and B (local position x and y). Turning the root off stops the effect; destroying it leaves its loose particles to finish.
static class Halo3ParticleEffects
{
	public const string RootTag = "halo3_effect";
	public const string LoopTag = "halo3_fx_loop";
	public const string ControlsName = "halo controls";

	// halo3_map_<map>: play the named map's copy of the effect (scenes tag their level's effects so).
	public const string MapTagPrefix = "halo3_map_";

	public static void Mark( GameObject root, string effectPath, bool looping, float a = 1, float b = 1, string mapName = null )
	{
		root.Tags.Add( RootTag );
		if ( !string.IsNullOrEmpty( mapName ) )
			root.Tags.Add( MapTagPrefix + mapName.ToLowerInvariant() );
		root.Tags.Add( Halo3ParticleLibrary.Token( effectPath ) );
		if ( looping )
			root.Tags.Add( LoopTag );

		if ( root.Components.Get<TemporaryEffect>( FindMode.EverythingInSelf ) is null )
		{
			// Never removes anything: it's there to be found (the scene only lists enabled components).
			var marker = root.AddComponent<TemporaryEffect>();
			marker.DestroyAfterSeconds = float.MaxValue;
			marker.WaitForChildEffects = false;
		}

		var controls = new GameObject( root, true, ControlsName );
		controls.LocalPosition = new Vector3( a, b, 0 );
		Halo3ParticleWorld.Watch();
	}
}
