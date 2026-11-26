using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// A model (hlmt) tag: what objects point at, naming the render model, collision model, animations and physics.
public sealed class ModelTag( TagEntry tag )
{
	public TagEntry Tag { get; } = tag;
	public TagEntry RenderModel => Live( Tag.Data.Reference( 0x00 ), "mode" );
	public TagEntry CollisionModel => Live( Tag.Data.Reference( 0x10 ), "coll" );
	public TagEntry Animation => Live( Tag.Data.Reference( 0x20 ), "jmad" );
	public TagEntry PhysicsModel => Live( Tag.Data.Reference( 0x30 ), "phmo" );
	public TagEntry LodRenderModel => Live( Tag.Data.Reference( 0x54 ), "mode" );
	public int VariantCount => Tag.Data.Count( 0x64 );

	// The model's materials (+0x7C, 0x14): material name -> global material name, which collision surfaces name.
	public Dictionary<string, string> GlobalMaterials()
	{
		var result = new Dictionary<string, string>();
		foreach ( var m in Tag.Data.Block( 0x7C, 0x14 ) )
		{
			if ( m.StringId( 0 ) is { Length: > 0 } name )
				result.TryAdd( name, m.StringId( 0xC ) );
		}
		return result;
	}

	static TagEntry Live( TagEntry tag, string group ) => tag is { Address: not 0 } && tag.Class == new TagClass( group ) ? tag : null;
}
