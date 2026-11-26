using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// The camouflaged variant of every object model, <c>mount://halo2/&lt;hlmt path&gt;_camo.vmdl</c>, as the Halo CE mount's
/// <c>_camo</c> models: the same model whose opaque materials use halo2_opaque_camo.shader, drawn after the frame buffer
/// copy with MCC's active camouflage over them by the renderer's <c>HaloCamo</c> attribute (0-1; from 255/256 only the
/// camouflage, bent through each part's camo bump map). A game swaps a unit's model (and its held weapon's: MCC
/// camouflages a unit's children with it, but carried flags, balls and bombs) for this while its camouflage is above 0;
/// transparent parts stay as they are (the *_active_camo templates fade by 1 - camouflage). The camouflage itself is the
/// unit's (MCC: unit +0x2FC, ramping at 0.25 a second when the powerup turns it on or off, less 0.1-1 per shot by the
/// weapon, never below 0.05 while on).
/// </summary>
sealed class CamoModelLoader( string mapPath, uint tagId ) : Halo2Loader
{
	/// <summary>The resource name of a model's camouflaged variant.</summary>
	public static string ResourceName( string modelResource ) => $"{modelResource}_camo";

	protected override Task<object> LoadAsync()
	{
		// As RenderModelLoader: build on the main thread.
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult( Load() );

		return LoadOnMainThread();
	}

	async Task<object> LoadOnMainThread()
	{
		await GameTask.MainThread();
		return Load();
	}

	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( tag is null || tag.Class != ModelTag.Class )
			return null;

		var hlmt = new ModelTag( tag );
		if ( hlmt.RenderModel is not { Address: not 0 } renderModel || renderModel.Class != RenderModelTag.Class )
			return null;

		try
		{
			return RenderModelBuilder.Build( Path, new RenderModelTag( renderModel ), hlmt, Halo2Mount.WorldScale, ModelLighting.Object, variantColors: true, camouflaged: true );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"Couldn't build the camouflaged {tag}: {e.Message}" );
			return null;
		}
	}
}
