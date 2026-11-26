using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// What a light volume's function reads (ltvl input variable enum).
public enum LightVolumeInput : byte
{
	ProfilePosition = 0,
	GameTime = 1,
	Age = 2,
	Random = 3,
	Correlation1 = 4,
	Correlation2 = 5,
	Lod = 6,
	EffectAScale = 7,
	EffectBScale = 8,
}

// One light volume of a light volume system tag (ltvl block 0x0, 0x17C): a row of profiles (sprites) along its forward, as MCC's light_volume_update compute shader lays them out: count from length x density, each at offset + i x spacing, sized, coloured and faded by functions of its profile position (0-1 along the row).
// Its render method (shaders\light_volume: albedo, blend_mode, fog) starts at +0x4 with the layout of ShaderTag's.
public sealed class LightVolume
{
	public const int Size = 0x17C;

	public string Name { get; }
	public ShaderTag RenderMethod { get; }
	public RenderMethodDefinition Definition { get; }
	public IReadOnlyList<int> Options { get; }
	public TagEntry BaseMap { get; }
	public int BaseMapIndex { get; }
	public int AppearanceFlags { get; }
	public float BrightnessRatio { get; }
	public ParticleProperty Length { get; }
	public ParticleProperty Offset { get; }
	public ParticleProperty ProfileDensity { get; }
	public ParticleProperty ProfileLength { get; }
	public ParticleProperty ProfileThickness { get; }
	public ParticleProperty ProfileColor { get; }
	public ParticleProperty ProfileAlpha { get; }
	public ParticleProperty ProfileIntensity { get; }

	public LightVolume( TagEntry tag, TagView e )
	{
		Name = e.StringId( 0x0 ) ?? "";
		var rm = e.At( 0x4 );
		RenderMethod = ShaderTag.Get( tag, rm );
		Definition = RenderMethodDefinition.Get( rm.Reference( 0 ) );
		Options = rm.Block( 0x10, 2 ).Select( o => (int)o.I16( 0 ) ).ToList();

		// The postprocess's base_map: its texture slot named in the template's texture names (rmt2 +0x6C).
		var postprocess = rm.Block( 0x28, 0x8C );
		if ( postprocess.Count > 0 )
		{
			var pp = postprocess[0];
			var template = pp.Reference( 0 );
			var names = template is null || template.Data.IsNull ? [] : template.Data.Block( 0x6C, 4 ).Select( s => s.StringId( 0 ) ).ToList();
			var textures = pp.Block( 0x10, 0x1C );
			var slot = names.IndexOf( "base_map" );
			if ( slot < 0 && textures.Count > 0 )
				slot = 0;
			if ( slot >= 0 && slot < textures.Count )
			{
				BaseMap = textures[slot].Reference( 0 );
				BaseMapIndex = textures[slot].I16( 0x10 );
			}
		}

		AppearanceFlags = e.U16( 0x44 );
		BrightnessRatio = e.F32( 0x48 );
		Length = ParticleProperty.Read( e, 0x4C );
		Offset = ParticleProperty.Read( e, 0x6C );
		ProfileDensity = ParticleProperty.Read( e, 0x8C );
		ProfileLength = ParticleProperty.Read( e, 0xAC );
		ProfileThickness = ParticleProperty.Read( e, 0xCC );
		ProfileColor = ParticleProperty.Read( e, 0xEC );
		ProfileAlpha = ParticleProperty.Read( e, 0x10C );
		ProfileIntensity = ParticleProperty.Read( e, 0x12C );
	}

	// The chosen option of a category (null if the definition has no such category).
	public string Option( string category )
	{
		if ( Definition is null )
			return null;
		var i = Definition.CategoryIndex( category );
		if ( i < 0 )
			return null;
		var option = i < Options.Count ? Options[i] : 0;
		var names = Definition.Categories[i].Options;
		return option >= 0 && option < names.Count ? names[option] : null;
	}
}

// A light volume system tag (ltvl).
public sealed class LightVolumeTag
{
	public static readonly TagClass Class = new( "ltvl" );

	public TagEntry Tag { get; }
	public IReadOnlyList<LightVolume> Volumes { get; }

	public LightVolumeTag( TagEntry tag )
	{
		Tag = tag;
		Volumes = tag.Data.Block( 0x0, LightVolume.Size ).Select( e => new LightVolume( tag, e ) ).ToList();
	}

	public static bool IsLightVolume( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;
}
