using System;
using Halo2.Data;

namespace Halo2.Combat;

// Turns Halo tag paths into the resource paths the Blam mount registers them under.
public static class HaloResources {
	// "objects/weapons/rifle/battle_rifle/battle_rifle.hlmt" -> "mount://halo2/objects/.../battle_rifle.vmdl".
	public static string ModelPath( string tagPath ) => Mounted( tagPath, ".vmdl" );

	// The mount's "_perm" variant, drawing every part with that bitmap index; 0 is the plain model.
	public static string ModelPath( string tagPath, int shaderPermutation ) =>
		shaderPermutation > 0 ? Mounted( tagPath, $"_perm{shaderPermutation}.vmdl" ) : ModelPath( tagPath );

	// The mount's sound event for a sound tag; it picks a random permutation.
	public static string SoundPath( string tagPath ) => Mounted( tagPath, ".sound" );

	public static string TexturePath( string bitmapPath ) => Mounted( bitmapPath, ".vtex" );

	public static string MapScene( string mapName ) => $"mount://{HaloMountCheck.Ident}/maps/{mapName}.scene";

	static string Mounted( string tagPath, string extension ) {
		if ( string.IsNullOrEmpty( tagPath ) ) return null;

		var path = tagPath.Replace( '\\', '/' );
		var dot = path.LastIndexOf( '.' );
		if ( dot > path.LastIndexOf( '/' ) )
			path = path[..dot];

		return $"mount://{HaloMountCheck.Ident}/{path}{extension}";
	}

	// Null if the mount isn't enabled or doesn't have the model.
	public static Model LoadModel( string tagPath ) {
		var path = ModelPath( tagPath );
		if ( path is null ) return null;

		var model = Sandbox.Model.Load( path );
		return model is null || model.IsError ? null : model;
	}

	public static bool MountHas( string mountPath ) {
		try { return HaloMountCheck.Mount?.GetByPath( mountPath ) is not null; }
		catch { return false; }
	}

	// Puts a Halo model on a renderer, glass included, and returns the model it got or null.
	// The mount splits a model with transparent glass into "__body", "__glass_tint" and "__glass_reflect" parts.
	// The glass parts go on child renderers of the same kind, bone merged when skinned.
	public static Model SetModelWithGlass( ModelRenderer renderer, string tagPath ) => SetMountedModelWithGlass( renderer, ModelPath( tagPath ) );

	public static Model SetMountedModelWithGlass( ModelRenderer renderer, string vmdlPath ) {
		if ( !renderer.IsValid() ) return null;
		foreach ( var old in renderer.GameObject.Children.Where( x => x.Name.StartsWith( "glass " ) ).ToList() )
			old.Destroy();

		if ( vmdlPath is null || !vmdlPath.EndsWith( ".vmdl" ) ) return null;
		var stem = vmdlPath[..^".vmdl".Length];

		if ( !MountHas( $"{stem}__body.vmdl" ) ) {
			var model = Sandbox.Model.Load( vmdlPath );
			if ( model is null || model.IsError ) return null;
			renderer.Model = model;
			return model;
		}

		var body = Sandbox.Model.Load( $"{stem}__body.vmdl" );
		if ( body is null || body.IsError ) return null;
		renderer.Model = body;

		foreach ( var part in new[] { "glass_tint", "glass_reflect" } ) {
			var glass = Sandbox.Model.Load( $"{stem}__{part}.vmdl" );
			if ( glass is null || glass.IsError ) continue;

			var go = renderer.Scene.CreateLocalObject( $"glass {part}" );
			go.SetParent( renderer.GameObject, false );
			go.LocalTransform = Transform.Zero;
			ModelRenderer child;
			if ( renderer is SkinnedModelRenderer skinned ) {
				var s = go.AddComponent<SkinnedModelRenderer>();
				s.UseAnimGraph = false;
				s.BoneMergeTarget = skinned;
				child = s;
			}
			else {
				child = go.AddComponent<ModelRenderer>();
			}
			child.Model = glass;
			child.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
		return body;
	}

	// Shows or hides a renderer's glass parts, as for a held weapon in first person.
	public static void SetGlassVisible( ModelRenderer renderer, bool visible ) {
		if ( !renderer.IsValid() ) return;
		foreach ( var child in renderer.GameObject.Children )
			if ( child.Name.StartsWith( "glass " ) ) child.Enabled = visible;
	}

	// Shows one choice of a mount model's body group, which is a Halo region's permutation.
	// Runtime-built models get all-ones part masks, so ModelRenderer.SetBodyGroup would draw every choice at once.
	// Their meshes use one bit per choice through the parts in order.
	public static void SetBodyGroup( ModelRenderer renderer, string part, int choice ) {
		var model = renderer?.Model;
		if ( model is null ) return;

		var bit = 0;
		foreach ( var body in model.Parts.All ) {
			var count = body.Choices.Count;
			if ( body.Name == part ) {
				if ( choice < 0 || choice >= count || bit + count > 64 ) return;
				var partMask = (count == 64 ? ulong.MaxValue : (1UL << count) - 1) << bit;
				renderer.BodyGroups = (renderer.BodyGroups & ~partMask) | (1UL << (bit + choice));
				return;
			}
			bit += count;
		}
	}

	// Turns off every body group but the named ones.
	public static void HideBodyGroupsExcept( ModelRenderer renderer, params string[] keep ) {
		var model = renderer?.Model;
		if ( model is null ) return;

		var bit = 0;
		foreach ( var body in model.Parts.All ) {
			var count = body.Choices.Count;
			if ( !keep.Contains( body.Name ) && bit + count <= 64 )
				renderer.BodyGroups &= ~((count == 64 ? ulong.MaxValue : (1UL << count) - 1) << bit);
			bit += count;
		}
	}
}

public static class LocalObjects {
	public const string Tag = "halo_local";

	// An object that exists only on this machine, like effects and view models.
	// Never networked, and tagged so the map loader knows it isn't part of the level.
	public static GameObject CreateLocalObject( this Scene scene, string name ) {
		var go = scene.CreateObject();
		go.Name = name;
		go.NetworkMode = NetworkMode.Never;
		go.Tags.Add( Tag );
		return go;
	}
}

// A small deterministic random source, so every machine derives the same spread from a shot's seed.
public struct HaloRandom {
	uint _state;

	public HaloRandom( int seed ) {
		_state = (uint)seed * 2654435761u + 0x9E3779B9u;
		if ( _state == 0 ) _state = 1;
	}

	public uint NextUInt() {
		// xorshift32
		var x = _state;
		x ^= x << 13;
		x ^= x >> 17;
		x ^= x << 5;
		_state = x;
		return x;
	}

	public float Float() => (NextUInt() & 0xFFFFFF) / (float)0x1000000;
	public float Float( float min, float max ) => min + (max - min) * Float();

	// A uniformly random direction.
	public Vector3 Direction() {
		var z = Float( -1, 1 );
		var angle = Float() * MathF.PI * 2;
		var r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
		return new Vector3( r * MathF.Cos( angle ), r * MathF.Sin( angle ), z );
	}

	// A direction between the inner and outer cone angles in degrees around forward, the way Halo spreads a shot.
	public Vector3 InCone( Vector3 forward, float innerDegrees, float outerDegrees ) {
		if ( outerDegrees <= 0 ) return forward;

		var angle = Float( innerDegrees, MathF.Max( innerDegrees, outerDegrees ) ).DegreeToRadian();
		var spin = Float() * MathF.PI * 2;

		var right = MathF.Abs( forward.z ) < 0.99f ? Vector3.Cross( forward, Vector3.Up ).Normal : Vector3.Cross( forward, Vector3.Forward ).Normal;
		var up = Vector3.Cross( right, forward ).Normal;
		var offset = right * MathF.Cos( spin ) + up * MathF.Sin( spin );
		return (forward * MathF.Cos( angle ) + offset * MathF.Sin( angle )).Normal;
	}
}
