using System.Text.Json;

namespace Blam.Halo1.TagData;

// The globals' difficulty settings.
public static partial class TagLayouts
{
	const int DifficultyValueCount = 35;
	const int DifficultyLevelCount = 4;

	/// <summary>
	/// The globals' difficulty block, 0x284 bytes: a row of four floats (easy, normal, hard,
	/// impossible) for each of the 35 difficulty values in Halo's order (enemy damage, vitality,
	/// shield, recharge, the friend ones, infection forms, an unused row, rate of fire ... major
	/// upgrade 2, four unused rows), written as <c>Values: [[easy, normal, hard, impossible], ...]</c>.
	/// </summary>
	static TagStruct GlobalsDifficulty() => new( "globals difficulty", 0x284 ) { Extra = WriteDifficultyValues };

	static void WriteDifficultyValues( Utf8JsonWriter json, LayoutJsonWriter writer, uint address )
	{
		json.WriteStartArray( "Values" );
		for ( var value = 0; value < DifficultyValueCount; value++ )
		{
			json.WriteStartArray();
			for ( var level = 0; level < DifficultyLevelCount; level++ )
			{
				var f = writer.Data.F32( address + (uint)((value * DifficultyLevelCount + level) * 4) );
				json.WriteNumberValue( float.IsFinite( f ) ? f : 0f );
			}
			json.WriteEndArray();
		}
		json.WriteEndArray();
	}
}
