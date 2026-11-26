using System.Collections.Generic;
using Blam.Halo2.TagData;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// A player representation's first person body (the globals' "first person body"): the legs and body Halo 2 draws in the player's own view.
// Halo skins it with its unit's node matrices as they are, node for node, so it's only drawn when the unit's render model has as many nodes with the same node list checksum.
// Every region is drawn at its first permutation.
public sealed class FirstPersonBody
{
	static readonly TagClass GlobalsClass = new( "matg" );

	// The element of the globals' player representations it's from.
	public int Representation { get; }

	public RenderModelTag Body { get; }

	// The third person unit (a biped) and its model tag.
	public TagEntry Unit { get; }
	public ModelTag UnitModel { get; }

	// The unit's render model, whose skeleton the body is drawn on.
	public RenderModelTag Skeleton { get; }

	// Whether Halo draws the body on the unit.
	public bool Drawn => Body.Nodes.Count == Skeleton.Nodes.Count && Body.NodeListChecksum == Skeleton.NodeListChecksum;

	FirstPersonBody( int representation, RenderModelTag body, TagEntry unit, ModelTag unitModel, RenderModelTag skeleton )
	{
		Representation = representation;
		Body = body;
		Unit = unit;
		UnitModel = unitModel;
		Skeleton = skeleton;
	}

	// The first person bodies of a map's player representations, in the globals' order.
	// Representations without a body, or whose unit has no render model, are left out.
	public static IReadOnlyList<FirstPersonBody> All( CacheFile cache )
	{
		var globals = cache.Globals;
		if ( globals is null || globals.Address == 0 || globals.Class != GlobalsClass )
			return [];

		var field = TagLayouts.Globals.Field( "player representation" );
		var element = field.Struct;
		var bodies = new List<FirstPersonBody>();
		var index = 0;
		foreach ( var address in globals.Cache.TagData.Elements( globals.Address + (uint)field.Offset, element.Size ) )
		{
			var body = globals.Cache.GetTagReference( address + (uint)element.Field( "first person body" ).Offset );
			var unit = globals.Cache.GetTagReference( address + (uint)element.Field( "third person unit" ).Offset );
			var hlmt = ObjectTag.IsObject( unit ) ? new ObjectTag( unit ).Model : null;
			var model = hlmt is { Address: not 0 } && hlmt.Class == ModelTag.Class ? new ModelTag( hlmt ) : null;
			if ( body is { Address: not 0 } && body.Class == RenderModelTag.Class && model?.RenderModel is { Address: not 0 } skeleton && skeleton.Class == RenderModelTag.Class )
				bodies.Add( new FirstPersonBody( index, new RenderModelTag( body ), unit, model, new RenderModelTag( skeleton ) ) );
			index++;
		}

		return bodies;
	}
}
