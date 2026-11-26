using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// The lights, lens flares and effects a placed object carries on parts its animation moves, built
/// on the object so they move with them: Infinity's beam emitters, gears whose 15 second position
/// animation carries the plasma ball (and its light and flare) from the shaft it sits in up into
/// the sky. Everything else an object carries is baked into the level's lights and flare model, or
/// placed as the level's ambient effects, where its markers are at the default pose (see
/// <see cref="LevelEffects"/>).
/// </summary>
/// <remarks>
/// Each one is a child of a GameObject named after the model attachment it sits on, which the
/// object's renderer drives from its animation (<see cref="ModelRenderer.CreateAttachments"/> finds
/// existing children by name). Built as the attachments prefab builds them (<see cref="AttachmentsPrefabLoader"/>):
/// effects and particle systems are played by <see cref="HaloParticleWorld"/>, carried by the child
/// (see <see cref="HaloEffects"/>). A looping effect is carried when any of its locations is on a
/// moving part, and each location rides the model attachment it's at (<see cref="EffectLocationSite"/>,
/// found by the runtime through <see cref="HaloEffects.LocateGroup"/>): the beam emitter's effect
/// smokes at the rising ball and at the shaft it rises from.
/// Looping sounds stay at the object (see <see cref="AmbientSoundBuilder"/>).
/// <para>A campaign's devices are posed by the game (doors opening), so their lights' lens flares
/// are built on them too, posed: only the flares, while the lights themselves stay in the level's
/// light plan, which keeps a10's hundreds of door lock lights within s&amp;box's per-cluster light
/// budget. Halo draws a light's flare at its marker every frame, so an open door's lock light flares
/// rise into the frame with it instead of hanging in the doorway.</para>
/// </remarks>
static class MovingAttachments
{
	/// <summary>
	/// Builds the attachments on parts of <paramref name="placement"/> that the looping animation it
	/// plays moves, or with <paramref name="posed"/> the lens flares of lights on parts the animation
	/// a game poses it by moves.
	/// </summary>
	/// <param name="lensFlare">A light's lens flare model tinted by a change color (white for none), or null.</param>
	/// <returns>The attachments (by index in the object's attachments block) it built, for the level to leave out.</returns>
	public static HashSet<int> Build( CacheFile cache, ObjectPlacement placement, SkinnedModelRenderer renderer, ObjectAnimationPicker.Choice choice, Func<TagEntry, System.Numerics.Vector3, Model> lensFlare, bool posed = false )
	{
		var built = new HashSet<int>();
		var modelTag = ObjectTags.GetObjectModel( cache, placement.Object );
		// Looping animations (gears, swaying and spinning scenery) carry everything. A device placed
		// open holds the end of its animation, which only a posed build covers.
		if ( (!choice.Looping && !posed) || choice.Animation is null || choice.Graph is null || modelTag is null || modelTag.Class != TagClasses.GbxModel )
			return built;

		var model = new GbxModelTag( cache, modelTag );
		var moving = MovingNodes( model, choice.Graph, choice.Animation );
		if ( !moving.Any( m => m ) )
			return built;

		// The node each model attachment is on, named as the model names them.
		var nodes = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (name, instance) in ModelMarkerNames.Name( model, ObjectAttachmentPlan.TakenNames( cache, model ) ) )
			nodes.TryAdd( name, instance.Node );

		var root = renderer.GameObject;
		bool Moves( string attachment ) => nodes.TryGetValue( attachment, out var node ) && node >= 0 && node < moving.Length && moving[node];

		// Where the attachment is at the default pose, until the renderer moves it; the object's origin for none.
		GameObject Anchor( string attachment, Transform onModel )
		{
			if ( attachment == "" )
				return root;

			var anchor = root.Children.FirstOrDefault( c => string.Equals( c.Name, attachment, StringComparison.OrdinalIgnoreCase ) )
				?? new GameObject( root, true, attachment );
			anchor.LocalTransform = onModel;
			return anchor;
		}

		foreach ( var child in ObjectAttachmentPlan.Plan( cache, placement.Object ) )
		{
			if ( child.Kind is not (AttachmentKind.Light or AttachmentKind.LensFlare or AttachmentKind.Effect or AttachmentKind.ParticleSystem) )
				continue;
			if ( posed && (child.Kind is not (AttachmentKind.Light or AttachmentKind.LensFlare) || child.LensFlare is null) )
				continue;
			// The game makes and drives these (see ObjectAttachmentSites.GameDriven).
			if ( child.Index >= 0 && ObjectAttachmentSites.GameDriven( cache, placement, child.Type, child.PrimaryScale, child.ChangeColor ) )
				continue;
			if ( !(child.Locations?.Any( s => Moves( s.ModelAttachment ) ) ?? Moves( child.ModelAttachment )) )
				continue;

			var at = new Transform( (Vector3)child.Position * HaloCeMount.WorldScale, GbxSkeleton.ToRotation( child.Rotation ) );
			var lit = child.Kind is AttachmentKind.Light or AttachmentKind.LensFlare;
			var go = new GameObject( Anchor( child.ModelAttachment, at ), lit || child.OnAtRest, child.Name );
			foreach ( var name in child.Tags )
				go.Tags.Add( name );

			var type = child.Type;
			try
			{
				switch ( child.Kind )
				{
					case AttachmentKind.Light:
					case AttachmentKind.LensFlare:
						var tint = LensFlares.ChangeColorTint( cache, placement, child.ChangeColor );
						var flare = child.LensFlare is not null ? lensFlare( type, tint ) : null;
						EffectBuilder.AddAttachedLight( cache, type, go, child.RestValue, flare, withLight: child.Kind == AttachmentKind.Light && !posed, tint );
						break;

					case AttachmentKind.Effect:
						EffectBuilder.Build( new EffectPlan( cache, type, EffectView.ThirdPerson, looping: true ), go, null, child.Locations );

						// Each location rides the model attachment it's at, tagged for the runtime to find under the object.
						var group = HaloEffects.LocateGroup( go );
						root.Tags.Add( HaloEffects.LocationScopeTag );
						foreach ( var (location, site) in EffectBuilder.PlaceAttachedLocations( go, at, child.Locations ) )
						{
							location.SetParent( Anchor( site.ModelAttachment, EffectBuilder.OnModel( site ) ), false );
							location.LocalTransform = Transform.Zero;
							location.Tags.Add( group );
						}

						HaloEffects.Mark( go, type, looping: true, attached: true, a: child.RestValue, color: LensFlares.ChangeColorTint( cache, placement, child.ChangeColor ) );
						break;

					case AttachmentKind.ParticleSystem:
						HaloEffects.Mark( go, type, looping: true, attached: true, a: child.RestValue, color: LensFlares.ChangeColorTint( cache, placement, child.ChangeColor ) );
						break;
				}
			}
			catch ( Exception e )
			{
				HaloCeMount.Current?.Logger.Warning( $"{placement.Object}: couldn't build attachment {type}: {e.Message}" );
			}

			built.Add( child.Index );
		}

		// After the anchors exist, so the renderer takes them over instead of making its own.
		if ( built.Count > 0 )
			renderer.CreateAttachments = true;

		return built;
	}

	/// <summary>
	/// Which of a model's nodes an animation moves away from the default pose in some frame, along
	/// with everything hanging from them.
	/// </summary>
	static bool[] MovingNodes( GbxModelTag model, ModelAnimationsTag graph, ModelAnimation animation )
	{
		var moving = new bool[model.Nodes.Count];
		if ( !animation.CanDecode )
			return moving;

		// Graphs without node names animate the model's nodes in order (as GbxSkeleton maps them).
		var map = graph.NodeNames.Count == 0 ? Enumerable.Range( 0, model.Nodes.Count ).ToArray() : FirstPersonWeapon.MapNodes( model, graph );
		var frames = animation.ReadFrames();
		for ( var i = 0; i < model.Nodes.Count; i++ )
		{
			var node = model.Nodes[i];
			var g = map[i];
			if ( g >= 0 && g < animation.NodeCount )
			{
				for ( var f = 0; f < animation.FrameCount && !moving[i]; f++ )
				{
					var pose = frames[f * animation.NodeCount + g];
					moving[i] = System.Numerics.Vector3.Distance( pose.Translation, node.Translation ) > 0.001f
						|| MathF.Abs( System.Numerics.Quaternion.Dot( pose.Rotation, node.Rotation ) ) < 0.9999f;
				}
			}

			// Parents come first (see AnimationSkeleton.HasSkeleton).
			if ( node.ParentIndex >= 0 && node.ParentIndex < i && moving[node.ParentIndex] )
				moving[i] = true;
		}

		return moving;
	}
}
