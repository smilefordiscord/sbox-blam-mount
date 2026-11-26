namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Keeps the PRT light of objects that turn their only node (Zanzibar's wheel blades and flywheels, gears with PRT
/// shaders) in that node's frame, every frame, as MCC does: their transfer vectors are in the node's rest frame, so a
/// light left in the object's frame would turn with them (a blade's lit side staying lit at the bottom of the wheel).
/// Called from <see cref="SceneUpkeep"/>; the objects are tagged <see cref="Tag"/> by <see cref="Halo2ObjectAnimations"/>.
/// </summary>
static class AnimatedPrt
{
	public const string Tag = "halo2_animated_prt";

	public static void Update( Scene scene )
	{
		foreach ( var go in scene.FindAllWithTag( Tag ) )
		{
			if ( go.GetComponent<SkinnedModelRenderer>() is not { Model: { BoneCount: > 0 } model } renderer )
				continue;

			var index = ObjectLightingTable.IndexOf( renderer.Tint );
			var root = model.Bones.AllBones[0];
			if ( index <= 0 || !renderer.TryGetBoneTransform( root.Name, out var bone ) )
				continue;

			// The node's turn from its rest pose, in the object's frame.
			var turn = renderer.WorldRotation.Inverse * bone.Rotation * root.LocalTransform.Rotation.Inverse;
			ObjectLightingTable.TurnPrt( index, (System.Numerics.Vector3)turn.Forward, (System.Numerics.Vector3)turn.Left, (System.Numerics.Vector3)turn.Up );
		}
	}
}
