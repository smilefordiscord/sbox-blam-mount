using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A weapon's first person view model: the player's hands and the weapon's first person model in
/// one skeleton, the nodes of the weapon's first person animation graph, with all of that graph's
/// animations. Registered per weapon tag as <c>&lt;weapon tag&gt;_fp.vmdl</c>.
/// </summary>
/// <remarks>
/// The model's origin is the camera, x forward and z up, as Halo poses it: parent it to the camera
/// with no offset. Animations keep their Halo names ("first-person idle", "first-person fire-1").
/// Halo picks them by slot rather than by name, and a few slots use an animation named for another
/// (the assault rifle reloads with "first-person reload-full" whether empty or not), so every slot
/// is also playable as "first-person &lt;slot&gt;" (see <see cref="FirstPersonAnimations.Names"/>).
/// "first-person idle" comes first, so it's the default sequence. Overlays ("moving", "overlays",
/// "overcharged-jitter", "ammunition") are baked onto the idle pose to play on their own.
///
/// Bones are bound in each model's own default pose (the hands' arms out straight, the weapon at
/// the origin), which is how the vertices are stored. Binding at the idle pose instead would show
/// better without an animation, but blended vertices would then be up to 0.017 world units off in
/// other animations.
///
/// Both models' markers are attachments on their bones (see <see cref="ModelMarkers"/>), the
/// weapon's first, so "primary trigger" is the muzzle and "primary ejection" the ejection port.
/// </remarks>
static class FirstPersonModel
{
	/// <summary>
	/// The resource name for a weapon tag's view model.
	/// </summary>
	public static string ResourceName( string weaponTagPath ) => $"{HaloCeMount.ResourcePath( weaponTagPath )}_fp";

	/// <param name="camouflaged">The variant for a camouflaged player (see <see cref="GbxModelBuilder.Meshes.Camouflaged"/>).</param>
	/// <param name="skin">A weapon skin for the weapon's surfaces (see <see cref="WeaponSkins"/>).</param>
	public static Model Build( HaloCeMount host, string name, CacheFile cache, FirstPersonSkeleton skeleton, float scale = HaloCeMount.WorldScale, bool camouflaged = false, WeaponSkin skin = null )
	{
		var builder = Model.Builder.WithName( name );
		var meshes = new GbxModelBuilder.Meshes( host, name, cache, builder, scale, collision: false, meshBounds: null, GbxModelBuilder.Parts.All )
		{
			Camouflaged = camouflaged,
			Skin = skin,
			SkinCubeMap = skin is null ? null : FirstCubeMap( cache, skeleton.WeaponModel ),
		};
		meshes.Add( skeleton.HandsModel, bones: skeleton.HandsBones );
		meshes.Add( skeleton.WeaponModel, bones: skeleton.WeaponBones );
		if ( meshes.Count == 0 )
			return null;

		meshes.Finish();

		var graph = skeleton.Graph;
		var names = GbxSkeleton.BoneNames( skeleton.GraphNodes.Select( n => graph.NodeNames[n] ) );
		var handsPose = GbxSkeleton.DefaultPose( skeleton.HandsModel, scale );
		var weaponPose = GbxSkeleton.DefaultPose( skeleton.WeaponModel, scale );
		var world = skeleton.BindNodes.Select( x => x.Node < 0 ? Transform.Zero : x.Hands ? handsPose[x.Node] : weaponPose[x.Node] ).ToArray();
		GbxSkeleton.AddBones( builder, names, skeleton.Parents, world, meshes.SkinnedVertices );

		// The weapon's markers ("primary trigger" at the muzzle, "primary ejection"), then the hands'.
		var taken = new HashSet<string>( names, StringComparer.OrdinalIgnoreCase );
		ModelMarkers.Add( builder, skeleton.WeaponModel, skeleton.WeaponBones.Select( b => names[b] ).ToArray(), null, scale, taken );
		ModelMarkers.Add( builder, skeleton.HandsModel, skeleton.HandsBones.Select( b => names[b] ).ToArray(), null, scale, taken );

		// Every animation node is a bone, so nothing keeps the bind pose; overlays go over the idle.
		var map = skeleton.BoneOfGraphNode.ToArray();
		var bind = world.Select( ( x, b ) => skeleton.Parents[b] < 0 ? x : world[skeleton.Parents[b]].ToLocal( x ) ).ToArray();
		var slots = graph.FirstPersonAnimations;
		var idle = Animation( graph, slots.Count > FirstPersonAnimations.Idle ? slots[FirstPersonAnimations.Idle] : -1 );
		var idlePose = idle is not null && idle.Type != AnimationType.Overlay ? GbxSkeleton.ReadPose( idle, 0, map, bind, scale ) : bind;

		foreach ( var (animationName, animation, looping) in FirstPersonAnimations.Named( graph ) )
		{
			if ( animation.CanDecode && animation.NodeCount == map.Length )
				GbxSkeleton.AddAnimation( builder, animationName, animation, map, idlePose, idlePose, scale, looping, graph.Tag, host.Logger );
			else
				host.Logger.Warning( $"{graph.Tag}: can't use animation {animation.Name}" );
		}

		return builder.Create();
	}

	static ModelAnimation Animation( ModelAnimationsTag graph, int index ) =>
		index >= 0 && index < graph.Animations.Count ? graph.Animations[index] : null;

	/// <summary>
	/// The first reflection cube map among a model's model shaders, for skinned parts whose shader
	/// has none (the pistol's dull black).
	/// </summary>
	static TagEntry FirstCubeMap( CacheFile cache, GbxModelTag model ) =>
		model.Shaders
			.Where( s => s.Shader?.Class == TagClasses.ShaderModel )
			.Select( s => new ShaderModelTag( cache, s.Shader ).ReflectionCubeMap )
			.FirstOrDefault( c => c is not null );
}

/// <summary>
/// Loads a weapon's first person view model (see <see cref="FirstPersonModel"/>), optionally
/// camouflaged or with a weapon skin (see <see cref="WeaponSkins"/>).
/// </summary>
sealed class FirstPersonModelLoader( string mapPath, uint weaponTagId, bool camouflaged = false, WeaponSkin skin = null ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var weapon = FirstPersonWeapon.Find( cache, cache.GetTag( weaponTagId ) );
		var skeleton = weapon is null ? null : FirstPersonSkeleton.Create( cache, weapon );
		if ( skeleton is null )
		{
			Host.Logger.Warning( $"{Path}: the weapon's first person models don't fit its animation graph" );
			return null;
		}

		return FirstPersonModel.Build( Host, Path, cache, skeleton, camouflaged: camouflaged, skin: skin );
	}
}
