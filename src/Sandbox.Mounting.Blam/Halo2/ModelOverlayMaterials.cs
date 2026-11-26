using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// The materials Halo 2 draws over a whole unit's model, registered per model tag for a game to draw as a second
/// renderer of the model (bone merged onto the first, the material as its override), as the Halo CE mount does
/// with its plasma shields:
/// <list type="bullet">
/// <item><c>mount://halo2/&lt;hlmt path&gt;_shield.vmat</c> and <c>_shield_fp.vmat</c>: the damage info's shield
/// damaged shader (third and first person), see <see cref="ShieldMaterial"/>.</item>
/// <item><c>mount://halo2/&lt;hlmt path&gt;_overshield.vmat</c> and <c>_overshield_fp.vmat</c>: its overshield shader.</item>
/// </list>
/// Only models whose shaders the mount draws get them (the Master Chief's, elites', the Arbiter's, hunters', flood
/// elites', the heretic's, Tartarus' overshield not: it only writes Halo's bloom mask).
/// <para>When MCC draws them: the shield pass over every drawn part while the object's current
/// shield damage (object +0xF4, 0-1) is above 0: each hit adds its shield damage fraction (capped at 1), then it falls
/// linearly back to 0 (2 seconds by default); MCC's bipeds export it as <c>shield_hit</c> and <c>shield_glow_source</c>.
/// The overshield pass while the shield vitality (+0xF0) is above 1 (the powerup sets 3): <c>object_overshield_amount</c>
/// = clamp( vitality - 1, 0, 1 ). Both draw on camouflaged units too.</para>
/// </summary>
sealed class ModelOverlayMaterials
{
	public enum Kind { Shield, ShieldFirstPerson, Overshield, OvershieldFirstPerson }

	static readonly string[] Suffixes = ["_shield", "_shield_fp", "_overshield", "_overshield_fp"];

	readonly Dictionary<string, (string MapPath, uint Id, Kind Kind)> _materials = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// Notes the overlay materials of every model tag in a map (the first map to have a model provides them).
	/// </summary>
	public void Find( CacheFile cache )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != ModelTag.Class || string.IsNullOrEmpty( tag.Path ) || tag.Cache != cache || tag.Address == 0 )
				continue;

			var path = Halo2Mount.ResourcePath( tag.Path );
			if ( _materials.ContainsKey( path + Suffixes[0] ) || _materials.ContainsKey( path + Suffixes[2] ) )
				continue;

			ModelTag model;
			try
			{
				model = new ModelTag( tag );
			}
			catch ( Exception )
			{
				continue;
			}

			for ( var k = 0; k < Suffixes.Length; k++ )
			{
				var shader = Shader( model, (Kind)k );
				if ( shader is not null && ShieldCurves.IsShield( new ShaderTag( shader ) ) )
					_materials[path + Suffixes[k]] = (cache.FilePath, tag.Id, (Kind)k);
			}
		}
	}

	public void Register( MountContext context )
	{
		foreach ( var (path, (mapPath, id, kind)) in _materials )
			context.Add( ResourceType.Material, path, new ModelOverlayMaterialLoader( mapPath, id, kind ) );
	}

	public static TagEntry Shader( ModelTag model, Kind kind ) => kind switch
	{
		Kind.Shield => model.ShieldShader,
		Kind.ShieldFirstPerson => model.ShieldFirstPersonShader,
		Kind.Overshield => model.OvershieldShader,
		_ => model.OvershieldFirstPersonShader,
	};
}

/// <summary>
/// Loads one of a model tag's overlay materials (<see cref="ModelOverlayMaterials"/>).
/// </summary>
sealed class ModelOverlayMaterialLoader( string mapPath, uint tagId, ModelOverlayMaterials.Kind kind ) : Halo2Loader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( tag is null || tag.Class != ModelTag.Class )
			return null;

		var shader = ModelOverlayMaterials.Shader( new ModelTag( tag ), kind );
		return shader is null ? null : ShieldMaterial.Create( Path, new ShaderTag( shader ), named: true );
	}
}
