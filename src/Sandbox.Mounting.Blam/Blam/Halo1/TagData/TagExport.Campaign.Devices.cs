using System.Text.Json;
using Blam.IO;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	/// <summary>
	/// A device placement's own fields after its groups and flags: a control's custom name (an index
	/// into the scenario's custom object names, -1 for none: the prompt then names the object), a
	/// light fixture's color, intensity and falloff and cutoff angles (radians).
	/// </summary>
	static void WriteDevicePlacement( Utf8JsonWriter json, string kind, MemoryRegion d, uint e )
	{
		if ( kind == "control" )
		{
			// Stored one up, 0 for none.
			json.WriteNumber( "CustomName", d.I16( e + 0x34 ) - 1 );
		}
		else if ( kind == "light fixture" )
		{
			WriteVector( json, "Color", d, e + 0x30, 3 );
			json.WriteNumber( "Intensity", d.F32( e + 0x3C ) );
			json.WriteNumber( "FalloffAngle", d.F32( e + 0x40 ) );
			json.WriteNumber( "CutoffAngle", d.F32( e + 0x44 ) );
		}
	}
}
