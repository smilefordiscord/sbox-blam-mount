namespace Sandbox.Mounting.Blam.Halo2;

// How prefabs and scenes ask Halo2ParticleWorld to play a Halo 2 effect with engine components only. The root is tagged halo2_effect and the effect's token (halo2_fx_loop for one that loops for ever, as objects carry them), and keeps a TemporaryEffect so it's found the frame it spawns. Its "halo controls" child holds effect scales A and B (local position x and y). Turning the root off stops the effect; destroying it leaves its loose particles to finish.
// An effect tagged halo2_fx_fp is seen in its local player's own first person view.
// That picks the particle systems Halo makes for it.
// A game that plays an effect's sounds by its own rules gives the root a disabled child named "halo sound". As an event plays a sound part, the runtime clones that child under the part's place, named after the sound tag ("sound/.../x.snd!"), and turns the clone on; while the clone exists, that part isn't started again there.
static class Halo2ParticleEffects
{
	public const string RootTag = "halo2_effect";
	public const string LoopTag = "halo2_fx_loop";
	public const string FirstPersonTag = "halo2_fx_fp";
	public const string ControlsName = "halo controls";
	public const string SoundName = "halo sound";

	public static void Mark( GameObject root, string effectPath, bool looping, float a = 1, float b = 1, bool firstPerson = false )
	{
		root.Tags.Add( RootTag );
		root.Tags.Add( Halo2ParticleLibrary.Token( effectPath ) );
		if ( looping )
			root.Tags.Add( LoopTag );
		if ( firstPerson )
			root.Tags.Add( FirstPersonTag );

		if ( root.Components.Get<TemporaryEffect>( FindMode.EverythingInSelf ) is null )
		{
			// Never removes anything: it's there to be found (the scene only lists enabled components).
			var marker = root.AddComponent<TemporaryEffect>();
			marker.DestroyAfterSeconds = float.MaxValue;
			marker.WaitForChildEffects = false;
		}

		var controls = new GameObject( root, true, ControlsName );
		controls.LocalPosition = new Vector3( a, b, 0 );
		Halo2ParticleWorld.Watch();
	}
}
