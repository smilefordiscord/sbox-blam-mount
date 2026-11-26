using System;
using Halo2.Data;
using Halo2.Combat;

namespace Halo2.Combat;

// Identifies a projectile the same on every machine: shooter id, firing tick, and index within the shot.
// The host's corrections use it to find each machine's copy.
public readonly record struct ProjectileKey( int Shooter, int Tick, int Index ) {
	public bool Valid => Tick != 0;

	public ProjectileKey Nth( int n ) => this with { Index = Index + n };

	// Index ranges: each hand's two barrels' pellets, right then left, and a grenade.
	public static ProjectileKey Shot( int shooter, int tick, Hand hand, int barrel ) => new( shooter, tick, ((int)hand * 2 + barrel) * 32 );
	public static ProjectileKey Grenade( int shooter, int tick ) => new( shooter, tick, 128 );

	// The hand whose shot this is.
	public Hand Hand => Index is >= 64 and < 128 ? Hand.Left : Hand.Right;
}

// Every machine simulates its own copy from this with the same seed. Only the host's copy deals damage.
public struct ProjectileLaunch {
	public string Projectile;
	public Vector3 Origin;
	// Inches per second, including the shooter's inherited velocity.
	public Vector3 Velocity;
	public Guid Owner;
	public Guid Target;
	public string Weapon;
	// Halo draws a tracer contrail only every few rounds.
	public bool Tracer;
	// A thrown grenade, which also starts spinning.
	public bool Thrown;
	// Simulated but not seen or heard, for the host's copy of a shot the self-test client already shows.
	public bool Hidden;
	// The grenade's in-hand visual, carried on so its glow and hum continue as in Halo. Local only, never sent.
	public GameObject HeldVisual;
	// Seeds fuse, spin and bounces the same on every machine.
	public int Seed;
	public ProjectileKey Key;
	// Where the damage falls between its effect's bounds, a dual wielded shot's own; none is 1.
	public float? DamageScale;
}

// Halo 2's projectiles: flight, acceleration and guidance, arming, timers, material responses and detonation.
// Halo has no hitscan: bullets are fast projectiles, and "travels instantaneously" only gives one extra step at launch.
public sealed partial class ProjectileSimulation : GameObjectSystem<ProjectileSimulation> {
	const float MaxLifetime = 12f;

	// A projectile moving slower than this (wu/s) has come to rest.
	const float RestSpeed = 0.41f;

	// A surface whose normal points up more than this is a floor.
	const float FloorNormalZ = 0.3f;

	// "Faster when owned by player" projectiles fly half again as fast for a player.
	const float PlayerSpeedScale = 1.5f;

	readonly List<Projectile> _projectiles = new();

	public IReadOnlyList<Projectile> Active => _projectiles;

	public ProjectileSimulation( Scene scene ) : base( scene ) {
		Listen( Stage.StartUpdate, 11, Tick, "Halo2.Projectiles" );
		Listen( Stage.FinishUpdate, 10, UpdateVisuals, "Halo2.ProjectileVisuals" );
	}

	// Every shooter here is a player.
	public static float SpeedScale( ProjectileTag tag ) => tag.HasFlag( "faster when owned by player" ) ? PlayerSpeedScale : 1;

	// The fuse and arming advance by a fixed fraction a tick; a time under a tick means none, or armed at once.
	static float RatePerTick( float seconds ) => seconds * HaloUnits.TicksPerSecond >= 1 ? 1 / (seconds * HaloUnits.TicksPerSecond) : 0;

	public Projectile Spawn( ProjectileLaunch launch, bool authoritative, RewindContext rewind ) {
		using var perf = Halo2.Combat.HaloPerf.Measure( "Projectiles.Spawn" );
		var tag = HaloDataStore.Game?.Projectile( launch.Projectile );
		if ( tag is null ) return null;

		var direction = launch.Velocity.LengthSquared > 0.0001f ? launch.Velocity.Normal : Vector3.Forward;
		var p = new Projectile {
			Launch = launch,
			Tag = tag,
			Position = launch.Origin,
			Velocity = launch.Velocity,
			Authoritative = authoritative,
			Rewind = rewind ?? RewindContext.None,
			Owner = Halo2Unit.Find( launch.Owner ),
			Target = Halo2Unit.Find( launch.Target ),
			Random = new HaloRandom( launch.Seed ),
			Orientation = Rotation.LookAt( direction ),
			SpeedScale = SpeedScale( tag ),
			DamageScale = launch.DamageScale ?? 1,
		};
		p.Speed = tag.InitialVelocity * p.SpeedScale;
		p.PreviousPosition = p.Position;
		p.PreviousOrientation = p.Orientation;

		// A thrown grenade spins about a random axis.
		if ( launch.Thrown ) {
			var push = HaloUnits.ToWorldUnits( launch.Velocity.Length ) / HaloUnits.TicksPerSecond;
			p.SpinAxis = p.Random.Direction();
			p.SpinRate = push * p.Random.Float( 0, 1 ) * (MathF.PI / 2) * HaloUnits.TicksPerSecond;
		}

		var timer = tag.HasFlag( "detonation maximum time if attached" ) || tag.Timer is not { Length: > 1 } t ? tag.Timer?.FirstOrDefault() ?? 0 : p.Random.Float( t[0], t[1] );
		p.TimerRate = RatePerTick( timer );
		p.ArmRate = RatePerTick( tag.ArmingTime );
		p.GuidedAngularVelocity = p.Random.Float( tag.GuidedAngularVelocityLower, tag.GuidedAngularVelocityUpper ).DegreeToRadian();
		p.Impact = ImpactDebug.Track( p );

		if ( !launch.Hidden ) p.CreateVisual( Scene );
		_projectiles.Add( p );

		if ( tag.HasFlag( "travels instantaneously" ) ) {
			Simulate( p, HaloUnits.TickInterval );
			// A shot that hits on its launch step lays its contrail now, with its impact.
			if ( p.Dead ) p.DestroyVisual();
		}
		return p;
	}

	// The local player's living Spartan, who hears flyby sounds.
	Halo2Unit _listener;

	// One step per Halo tick on the frame clock (HaloTick), never Sandbox's fixed step.
	void Tick() {
		for ( var i = 0; i < HaloTick.Advanced; i++ ) Step();
	}

	void Step() {
		using var perf = Halo2.Combat.HaloPerf.Measure( "Projectiles.Step" );
		var dt = HaloUnits.TickInterval;
		_listener = Halo2Unit.Local is { IsAlive: true } local ? local : null;
		for ( var i = _projectiles.Count - 1; i >= 0; i-- ) {
			var p = _projectiles[i];
			p.Age += dt;
			if ( !p.Dead ) Simulate( p, dt );
			if ( p.Dead || p.Age > MaxLifetime ) {
				if ( p.Impact is not null ) ImpactDebug.Get( Scene )?.Finished( p );
				p.DestroyVisual();
				_projectiles.RemoveAt( i );
			}
		}
	}

	void UpdateVisuals() {
		using var perf = Halo2.Combat.HaloPerf.Measure( "Projectiles.Visuals" );
		var alpha = HaloTick.Fraction;
		foreach ( var p in _projectiles )
			p.UpdateVisual( alpha );
	}

	// One tick: arming and the timer, then flight until the tick is used up; a due detonation goes off once armed.
	void Simulate( Projectile p, float dt ) {
		p.PreviousPosition = p.Position;
		p.PreviousOrientation = p.Orientation;
		p.CollisionTime = null;
		var tag = p.Tag;

		p.Armed = MathF.Min( 1, p.Armed + p.ArmRate );
		FollowAttachment( p );
		if ( p.Dead ) return;

		var started = tag.DetonationTimerStarts switch {
			"after first bounce off floor" => p.BouncedOffFloor,
			"when at rest" => p.AtRest,
			"after first bounce off any surface" => p.HitAnything,
			_ => true,
		};
		if ( p.Stuck || p.AttachedTo is not null || p.TimerRunning || started ) {
			if ( !p.TimerRunning ) PlayDetonationStarted( p );
			p.TimerRunning = true;
			if ( p.TimerRate > 0 ) {
				p.Timer += p.TimerRate;
				if ( p.Timer >= 1 ) Raise( p, ProjectileState.Detonate );
			}
		}

		Fly( p, dt );

		if ( p.State == ProjectileState.Detonate && p.IsArmed ) Detonate( p );
		else if ( p.State == ProjectileState.Delete ) p.Dead = true;
	}

	void Fly( Projectile p, float dt ) {
		var tag = p.Tag;
		var gravity = HaloUnits.GravityPerSecond * tag.AirGravityScale;
		var flybyPlayed = false;
		var updateDt = dt;

		for ( var hits = 0; dt > 0; hits++ ) {
			// An unarmed projectile flies on until it can detonate.
			if ( p.State == ProjectileState.Delete || (p.State == ProjectileState.Detonate && p.IsArmed) ) break;
			if ( p.Stuck || p.AttachedTo is not null ) break;

			if ( p.SpinRate > 0 && !tag.HasFlag( "oriented along velocity" ) )
				p.Orientation = (Rotation.FromAxis( p.SpinAxis, (p.SpinRate * dt).RadianToDegree() ) * p.Orientation).Normal;

			var v0 = ToWu( p.Velocity );
			var v = v0;
			Guide( p, ref v, dt );

			// The speed runs from initial to final over the acceleration range, along the projectile's forward.
			Vector3 average;
			float averageSpeed;
			if ( tag.InitialVelocity != 0 && tag.FinalVelocity != 0 ) {
				var range = tag.AccelerationRange is { Length: > 1 } r ? r : new[] { 0f, 0f };
				var inverse = range[1] > range[0] ? 1 / (range[1] - range[0]) : 0;
				var t = Math.Clamp( (p.AccelerationDistance - p.SpeedScale * range[0]) * inverse, 0, 1 );
				var speed = p.SpeedScale * (tag.InitialVelocity + t * (tag.FinalVelocity - tag.InitialVelocity));
				v += p.Forward * (speed - p.Speed);
				p.Speed = speed;
				p.AccelerationDistance += speed * dt;
				average = 0.5f * (v0 + v);
				averageSpeed = 0.5f * (v.Length + v0.Length);
			}
			else {
				average = v;
				averageSpeed = v0.Length;
			}

			// Without a range or timer, the damage range is how far it goes.
			if ( tag.MaximumRange == 0 && tag.TimerMaximum == 0 && p.Distance >= tag.DamageRange )
				Raise( p, ProjectileState.Delete );

			v.z -= gravity * dt;
			average.z -= 0.5f * gravity * dt;

			var fraction = 1f;
			if ( tag.MaximumRange > 0 && p.Distance + averageSpeed * dt > tag.MaximumRange ) {
				fraction = averageSpeed > 0 ? (tag.MaximumRange - p.Distance) / (averageSpeed * dt) : 0;
				Raise( p, ProjectileState.Detonate );
			}

			if ( hits == MaximumCollisionsPerUpdate ) {
				Raise( p, ProjectileState.Detonate );
				break;
			}

			var start = p.Position;
			var move = HaloUnits.ToInches( 1 ) * average * fraction * dt;
			var end = start + move;
			var length = move.Length;
			var direction = length > 0.0001f ? move / length : p.Forward;

			var world = WorldTrace( start, end );
			var worldDistance = world.Hit ? world.Distance : length;
			var itemHit = ItemTrace( start, direction, worldDistance, out var hitItem );
			if ( hitItem is not null ) worldDistance = itemHit.Distance;

			// The host tests the poses the shooter saw. The shooter is ignored until the first collision.
			var ignore = p.HitAnything ? p.Ignore : p.Owner;
			var unitHit = default( UnitHit );
			var hitUnit = length > 0.0001f && UnitTrace.Ray( Scene, start, direction, worldDistance, p.Authoritative ? p.Rewind : RewindContext.None, ignore, out unitHit )
				&& unitHit.Hit.Distance > 0;

			// Halo 2's dead bipeds block shots too, as they lie this tick.
			var corpse = default( SpartanCorpse );
			var corpseHit = default( HitboxHit );
			var hitCorpse = length > 0.0001f && SpartanCorpse.Trace( Scene, start, direction, hitUnit ? unitHit.Hit.Distance : worldDistance, p.IgnoreCorpse, out corpse, out corpseHit )
				&& corpseHit.Distance > 0;
			if ( hitCorpse ) {
				hitUnit = false;
				hitItem = null;
			}

			var travelled = hitUnit ? unitHit.Hit.Distance : hitCorpse ? corpseHit.Distance : hitItem is not null ? itemHit.Distance : world.Hit ? world.Distance : length;
			if ( !flybyPlayed ) flybyPlayed = Flyby( p, start, direction * travelled );
			p.Impact?.Step( Scene, p, start, direction, hitUnit ? length : travelled, hitUnit ? unitHit : null );

			if ( !hitUnit && !hitCorpse && hitItem is null && !world.Hit ) {
				dt = 0;
				p.Position = end;
				p.Velocity = FromWu( v );
				p.Distance += HaloUnits.ToWorldUnits( length );
				if ( tag.HasFlag( "oriented along velocity" ) && v.LengthSquared > 0 ) p.Orientation = Rotation.LookAt( v.Normal );
				break;
			}

			var hitFraction = length > 0 ? Math.Clamp( travelled / length, 0, 1 ) : 0;
			dt *= 1 - hitFraction;
			p.CollisionTime = updateDt - dt;
			p.HitAnything = true;
			p.Velocity = FromWu( Vector3.Lerp( v0, v, hitFraction ) );
			p.Distance += HaloUnits.ToWorldUnits( travelled );

			if ( hitUnit ) {
				p.Position = unitHit.Hit.Position;
				if ( unitHit.Hit.Normal.z > FloorNormalZ ) p.BouncedOffFloor = true;
				HitUnit( p, unitHit, direction );
			}
			else if ( hitCorpse ) {
				p.Position = corpseHit.Position;
				if ( corpseHit.Normal.z > FloorNormalZ ) p.BouncedOffFloor = true;
				HitCorpse( p, corpse, corpseHit, direction );
			}
			else if ( hitItem is not null ) {
				p.Position = itemHit.HitPosition;
				if ( itemHit.Normal.z > FloorNormalZ ) p.BouncedOffFloor = true;
				HitItem( p, itemHit, hitItem, direction );
			}
			else {
				p.Position = world.HitPosition + world.Normal * SurfaceSkin;
				if ( world.Normal.z > FloorNormalZ ) p.BouncedOffFloor = true;
				if ( p.Authoritative ) Halo2.Combat.HaloPhysics.HostProjectileHit( Scene, world.GameObject, world.HitPosition, direction, new DamageEvent {
					DamageEffect = p.Tag.ImpactDamage,
					Scale = ImpactDamageScale( p, world.HitPosition ),
					Position = world.HitPosition,
					Direction = direction,
					AttackerId = p.Launch.Owner,
					WeaponName = p.Launch.Weapon,
					Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
				}, HaloSurfaces.MaterialOrDefault( world.Surface ) );
				Respond( p, world.Normal, HaloSurfaces.MaterialOrDefault( world.Surface ), null, world.HitPosition );
			}

			if ( tag.HasFlag( "oriented along velocity" ) && p.Velocity.LengthSquared > 1 ) p.Orientation = Rotation.LookAt( p.Velocity.Normal );
		}
	}

	static void Raise( Projectile p, ProjectileState state ) {
		if ( state > p.State ) p.State = state;
	}

	static Vector3 ToWu( Vector3 inchesPerSecond ) => inchesPerSecond / HaloUnits.WorldScale;
	static Vector3 FromWu( Vector3 wuPerSecond ) => wuPerSecond * HaloUnits.WorldScale;

	// Guided projectiles turn toward their target a fixed angle a tick, if it's ahead or they can track behind.
	// Steering that adjusts orientation also points their forward, which their acceleration follows, at the target.
	void Guide( Projectile p, ref Vector3 v, float dt ) {
		if ( !p.Target.IsValid() || !p.Target.IsAlive || !(p.GuidedAngularVelocity > 0) || v.LengthSquared < 0.0001f ) return;
		var tag = p.Tag;
		var aim = UnitTrace.Center( p.Target, p.Authoritative ? p.Rewind : RewindContext.None );
		var to = ToWu( aim - p.Position );
		if ( tag.TargetedLeadingFraction > 0 && v.Length > 0 ) {
			var targetVelocity = ToWu( p.Target.MoveVelocity );
			var lead = Math.Clamp( targetVelocity.Length * Math.Clamp( to.Length / v.Length, 0, 2 ) * tag.TargetedLeadingFraction, 0, 2 );
			if ( targetVelocity.LengthSquared > 0 ) to += targetVelocity.Normal * lead;
		}
		if ( !tag.HasFlag( "can track behind itself" ) && Vector3.Dot( v, to ) <= 0 ) return;

		var axis = Vector3.Cross( v, to );
		if ( axis.LengthSquared > 0.000001f )
			v = Rotation.FromAxis( axis.Normal, (p.GuidedAngularVelocity * dt).RadianToDegree() ) * v;
		if ( tag.HasFlag( "steering adjusts orientation" ) && to.LengthSquared > 0 )
			p.Orientation = Rotation.LookAt( to.Normal );
	}

	// Plays the flyby sound at the nearest point of the move when it passes the listener within the sound's range.
	// The listener's projection must fall within the move, and the shooter hears nothing until the first collision.
	// Returns whether it played; it plays at most once per update.
	bool Flyby( Projectile p, Vector3 start, Vector3 movement ) {
		var sound = p.Tag.FlybySound;
		var listener = _listener;
		if ( string.IsNullOrEmpty( sound ) || listener is null || p.Launch.Hidden ) return false;
		if ( listener == p.Owner && !p.HitAnything ) return false;

		var lengthSquared = movement.LengthSquared;
		if ( lengthSquared < 1e-6f ) return false;

		var maximumDistance = HaloSounds.MaximumDistance( sound );
		if ( maximumDistance <= 0 ) return false;

		var toListener = listener.Center - start;
		var along = Vector3.Dot( toListener, movement );
		if ( along < 0 || along >= lengthSquared ) return false;

		var nearest = start + movement * (along / lengthSquared);
		var perpendicularWu = HaloUnits.ToWorldUnits( (start + toListener).Distance( nearest ) );
		if ( perpendicularWu >= maximumDistance ) return false;

		HaloSounds.Play( sound, nearest );
		return true;
	}

	// The response for a global material: its own entry, else its parent material's, up the chain; none means impact.
	static ProjectileMaterialResponseTag Response( ProjectileTag tag, int materialIndex ) {
		var globals = HaloDataStore.Game?.Globals;
		for ( var material = globals?.Material( materialIndex ); material is not null; material = globals.Material( globals.MaterialIndex( material.ParentName ) ) ) {
			var response = tag.MaterialResponses?.FirstOrDefault( x => x.MaterialName == material.Name );
			if ( response is not null ) return response;
		}
		return null;
	}

	// The potential response needs a chance, the impact's angle and speed within its bounds, and this kind of object.
	// An upper bound of 0 is any. Then it wins if the roll comes in under its chance; otherwise the default response plays.
	static string PickResponse( Projectile p, ProjectileMaterialResponseTag r, bool againstUnit, float speed, float angle ) {
		if ( r is null ) return "impact (detonate)";

		var ok = r.ChanceFraction > 0
			&& (r.Between is not { Length: > 1 } b || b[1] == 0 || (angle >= b[0] && angle <= b[1]))
			&& (r.And is not { Length: > 1 } a || a[1] == 0 || (speed >= a[0] && speed <= a[1]));
		if ( r.ResponseFlags?.Contains( "never against units" ) ?? false ) ok &= !againstUnit;
		if ( r.ResponseFlags?.Contains( "only against units" ) ?? false ) ok &= againstUnit;

		return ok && (r.ChanceFraction >= 1 || r.ChanceFraction > p.Random.Float()) ? r.PotentialResponse : r.DefaultResponse;
	}

	// A direct hit: the impact damage first, whatever the response, then the response for the material that took it.
	void HitUnit( Projectile p, UnitHit hit, Vector3 direction ) {
		var target = hit.Unit;
		var material = target.HitMaterial;
		if ( p.Authoritative && p.Tag.ImpactDamage is not null ) {
			var result = target.TakeDamage( new DamageEvent {
				DamageEffect = p.Tag.ImpactDamage,
				Scale = ImpactDamageScale( p, hit.Hit.Position ),
				Position = hit.Hit.Position,
				Direction = direction,
				MaterialName = hit.Hit.Material,
				Node = hit.Hit.Node,
				AttackerId = p.Launch.Owner,
				WeaponName = p.Launch.Weapon,
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
			} );
			if ( result.GlobalMaterial is not null ) material = HaloSurfaces.MaterialIndex( result.GlobalMaterial );
		}

		Respond( p, hit.Hit.Normal, material, hit, hit.Hit.Position );
	}

	// The impact damage's scale: the shot's, or with "damage scales based on distance" what's left of its damage range.
	static float ImpactDamageScale( Projectile p, Vector3 hitPosition ) {
		if ( !p.Tag.HasFlag( "damage scales based on distance" ) || p.Tag.DamageRange <= 0 ) return p.DamageScale;
		return Math.Clamp( (1 - p.Distance / p.Tag.DamageRange) * p.DamageScale, 0, 1 );
	}

	// A corpse's impact damage pushes it by the dead push rule; the struck armor's material answers, as on a unit.
	void HitCorpse( Projectile p, SpartanCorpse corpse, HitboxHit hit, Vector3 direction ) {
		if ( p.Authoritative && p.Tag.ImpactDamage is not null ) corpse.HostDamage( p.Tag.ImpactDamage, direction );
		var model = HaloDataStore.Game?.SpartanModel;
		var material = model?.Materials?.FirstOrDefault( x => x.MaterialName == hit.Material )?.GlobalMaterialName ?? model?.DamageInfo?.GlobalIndirectMaterialName;
		Respond( p, hit.Normal, HaloSurfaces.MaterialIndex( material ), null, hit.Position, corpse );
	}

	// The nearest loose weapon or equipment, via WorldItem's trigger collider that nothing else touches.
	SceneTraceResult ItemTrace( Vector3 start, Vector3 direction, float distance, out Halo2.Combat.WorldItem item ) {
		item = null;
		var tr = Scene.Trace.Ray( start, start + direction * distance ).HitTriggersOnly().WithTag( Halo2.Combat.WorldItem.ColliderTag ).Run();
		if ( !tr.Hit || tr.StartedSolid ) return tr;
		item = tr.GameObject?.GetComponentInParent<Halo2.Combat.WorldItem>();
		return tr;
	}

	// Impact damage pushes the item; even a tiny push spins a resting item and knocks a floating one down.
	void HitItem( Projectile p, SceneTraceResult tr, Halo2.Combat.WorldItem item, Vector3 direction ) {
		if ( p.Authoritative && p.Tag.ImpactDamage is not null )
			item.HostImpactDamage( p.Tag.ImpactDamage, ImpactDamageScale( p, tr.HitPosition ), direction );
		Respond( p, tr.Normal, HaloSurfaces.MaterialOrDefault( tr.Surface ), null, tr.HitPosition );
	}

	// The material's response, then noise on the new velocity; a stopped projectile rests, and on a floor stays.
	// A corpse that was hit counts as a unit.
	void Respond( Projectile p, Vector3 normal, int material, UnitHit? unit, Vector3 point, SpartanCorpse corpse = null ) {
		var tag = p.Tag;
		var r = Response( tag, material );
		var v = ToWu( p.Velocity );
		var direction = v.LengthSquared > 0.0001f ? v.Normal : -normal;
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( direction, normal ), -1, 1 ) ).RadianToDegree() - 90;
		if ( r is { AngularNoise: > 0 } ) angle += p.Random.Float( -r.AngularNoise, r.AngularNoise );
		var response = PickResponse( p, r, unit is not null || corpse is not null, v.Length, angle );
		p.HitMaterial = material;
		ImpactEffect( p, point, normal, material, response );

		var attached = false;
		switch ( response ) {
			case "fizzle":
			case "fizzle (ricochet)":
				v = Vector3.Zero;
				Raise( p, ProjectileState.Delete );
				break;
			case "overpenetrate" when unit is { } hit:
				v *= 1 - (r?.InitialFriction ?? 0);
				p.Ignore = hit.Unit;
				p.Position = point + direction * 2;
				break;
			case "overpenetrate" when corpse is not null:
				v *= 1 - (r?.InitialFriction ?? 0);
				p.IgnoreCorpse = corpse;
				p.Position = point + direction * 2;
				break;
			case "overpenetrate" when tag.TimerMaximum > 0:
				p.AtRest = true;
				p.BouncedOffFloor = true;
				v = Vector3.Zero;
				attached = true;
				StickToWorld( p, point, normal );
				break;
			case "attach":
				v = Vector3.Zero;
				attached = true;
				if ( unit is { } stuck ) Attach( p, stuck );
				else if ( corpse is not null ) AttachToCorpse( p, corpse, point, normal );
				else StickToWorld( p, point, normal );
				break;
			case "bounce (dud)":
				p.DamageScale = 0;
				v = Bounce( v, normal, r );
				break;
			case "bounce":
				v = Bounce( v, normal, r );
				break;
			default:
				v = Vector3.Zero;
				Raise( p, ProjectileState.Detonate );
				break;
		}

		if ( r is not null && v.LengthSquared > 0 ) {
			if ( r.AngularNoise > 0 ) {
				var axis = Vector3.Cross( v, p.Random.Direction() );
				if ( axis.LengthSquared > 0 ) v = Rotation.FromAxis( axis.Normal, p.Random.Float( 0, r.AngularNoise ) ) * v;
			}
			if ( r.VelocityNoise != 0 ) v = v.Normal * MathF.Max( 0, v.Length + p.Random.Float( -r.VelocityNoise, r.VelocityNoise ) );
		}

		if ( !attached && v.Length < tag.MinimumVelocity ) Raise( p, ProjectileState.Detonate );
		if ( v.Length < RestSpeed ) {
			p.AtRest = true;
			if ( normal.z > FloorNormalZ && unit is null && !attached ) {
				v = Vector3.Zero;
				StickToWorld( p, point, normal );
			}
			if ( tag.TimerMaximum == 0 ) Raise( p, ProjectileState.Detonate );
		}

		p.Velocity = FromWu( v );
		p.Bounces++;
	}

	// The tangent part keeps 1 - parallel friction, and the normal part flips and keeps 1 - perpendicular friction.
	static Vector3 Bounce( Vector3 v, Vector3 normal, ProjectileMaterialResponseTag r ) {
		var into = normal * Vector3.Dot( v, normal );
		var along = v - into;
		return along * (1 - (r?.ParallelFriction ?? 0)) - into * (1 - (r?.PerpendicularFriction ?? 0));
	}

	// The responses' names in the mount's roles, in the order of the material effects entries they use, from entry 15.
	// Fizzle (ricochet) uses bounce (dud)'s entry.
	static readonly string[] ResponseRoles = ["impact", "fizzle", "overpenetrate", "attach", "bounce", "bounce_dud"];

	// An entry with no effects or sounds falls back to another one, and attach to none.
	// Fizzle and overpenetrate fall back to impact, bounce to fizzle, and bounce (dud) to bounce.
	static readonly int[] ResponseFallback = [-1, 0, 0, -1, 1, 4];

	static int ResponseIndex( string response ) => response switch {
		"fizzle" => 1,
		"overpenetrate" => 2,
		"attach" => 3,
		"bounce" => 4,
		"bounce (dud)" or "fizzle (ricochet)" => 5,
		_ => 0,
	};

	// Halo 2 plays no material effect for a hit slower than this into the surface, in world units per second.
	const float MinimumImpactSpeed = 0.25f;

	// What the response plays on the material, every machine playing its own copy, near enough to a local camera:
	// 50 world units for an impact, 20 for the other responses.
	// The material's effect and secondary effect play as the mount's roles, with its sound and secondary sound.
	// With its sweetener on, the hit material's small sweetener sound plays too.
	void ImpactEffect( Projectile p, Vector3 point, Vector3 normal, int material, string response ) {
		if ( p.Launch.Hidden ) return;
		if ( -Vector3.Dot( ToWu( p.Velocity ), normal ) <= MinimumImpactSpeed ) return;
		var index = ResponseIndex( response );
		if ( !CameraWithin( point, index == 0 ? 50 : 20 ) ) return;

		var game = HaloDataStore.Game;
		while ( index >= 0 && (game?.MaterialEffectsEntry( p.Tag.MaterialEffects, MaterialEffectsTag.ProjectileImpact + index )?.IsEmpty ?? true) )
			index = ResponseFallback[index];
		if ( index < 0 ) return;

		var (effect, sound) = game.MaterialEffect( p.Tag.MaterialEffects, MaterialEffectsTag.ProjectileImpact + index, material );
		if ( effect is not null ) {
			var role = HaloEffects.Role( p.Launch.Projectile, $"{ResponseRoles[index]}_{effect.MaterialName}" );
			if ( effect.Tag is not null ) HaloEffects.Play( role, point, normal, p.Velocity );
			if ( effect.SecondaryTag is not null ) HaloEffects.Play( $"{role}_secondary", point, normal, p.Velocity );
		}
		if ( sound is not null ) {
			HaloSounds.Play( sound.Tag, point );
			HaloSounds.Play( sound.SecondaryTag, point );
			if ( sound.Sweetened ) HaloSounds.Play( game.Globals?.Material( material )?.SoundSweetenerSmall, point );
		}
	}

	bool CameraWithin( Vector3 point, float worldUnits ) => Scene.Camera is { } camera && HaloUnits.ToWorldUnits( camera.WorldPosition.Distance( point ) ) < worldUnits;

	static void StickToWorld( Projectile p, Vector3 point, Vector3 normal ) {
		p.Stuck = true;
		p.StuckNormal = normal;
		p.Position = point + normal * SurfaceSkin;
		p.SpinRate = 0;
	}

	// The "detonation started" effect, when the timer starts on something it rests on or sticks to.
	void PlayDetonationStarted( Projectile p ) {
		if ( p.Launch.Hidden || !(p.AtRest || p.Stuck || p.AttachedTo is not null) ) return;
		var effect = HaloEffects.Play( HaloEffects.Role( p.Launch.Projectile, "detonation_started" ), p.Position, p.StuckNormal, p.Velocity, source: p );
		if ( effect.IsValid() && p.AttachedTo is not null ) p.Carry( effect );
	}

	const int MaximumCollisionsPerUpdate = 10;

	// Halo nudges hit points 1/4096 world units off the surface when they'd land in the wrong BSP leaf.
	// We can't check leaves, so it always applies; otherwise a grenade sliding down a steep face can trace through it.
	public static readonly float SurfaceSkin = HaloUnits.ToInches( 1f / 4096f );

	// Skips players, triggers, projectiles, debris and ragdolls.
	public SceneTraceResult WorldTrace( Vector3 start, Vector3 end ) => Scene.Trace.Ray( start, end )
		.WithoutTags( "player", "trigger", "projectile", "debris", "ragdoll" )
		.Run();

	// Keeps a stuck projectile on its bone.
	// When its Spartan dies it rides the corpse; with the corpse gone, or none by the time the Spartan is back, it
	// detaches and detonates at once.
	void FollowAttachment( Projectile p ) {
		if ( p.AttachedTo is not { } target ) return;

		if ( target.IsValid() && !target.IsAlive && !p.SawDeath ) {
			p.SawDeath = true;
			p.CorpseDeath = 0;
		}
		if ( p.SawDeath && p.Corpse is null && target.IsValid() ) p.Corpse = SpartanCorpse.Find( Scene, target.GameObject.Id, p.CorpseDeath );

		var gone = p.Corpse is not null ? !p.Corpse.IsValid() : !target.IsValid() || (p.SawDeath && target.IsAlive);
		if ( gone ) {
			p.AttachedTo = null;
			p.Corpse = null;
			p.Stuck = true;
			p.Armed = 1;
			p.State = ProjectileState.Detonate;
			return;
		}

		// A corpse not yet here leaves it where it was.
		if ( p.SawDeath && p.Corpse is null ) return;

		var renderer = p.SawDeath ? p.Corpse.Renderer : target.Renderer;
		if ( p.AttachBone is not null && renderer.IsValid() && renderer.TryGetBoneTransform( p.AttachBone, out var frame ) ) {
			p.Position = frame.PointToWorld( p.AttachLocal.Position );
			p.Orientation = frame.Rotation * p.AttachLocal.Rotation;
		}
		else if ( p.AttachBone is null && !p.SawDeath ) {
			var root = target.Root;
			p.Position = root.Position + root.Rotation * p.AttachOffset;
			p.Orientation = root.Rotation * p.AttachLocal.Rotation;
		}
		// A body that can't be posed right now leaves it where it was.
	}

	// Null without a posed body.
	static (string Name, Transform Frame)? NearestBone( SkinnedModelRenderer renderer, Vector3 point ) {
		if ( !renderer.IsValid() || renderer.Model is null ) return null;

		(string Name, Transform Frame)? best = null;
		var bestDistance = float.MaxValue;
		foreach ( var bone in renderer.Model.Bones.AllBones ) {
			if ( !renderer.TryGetBoneTransform( bone, out var frame ) ) continue;
			var distance = frame.Position.DistanceSquared( point );
			if ( distance >= bestDistance ) continue;
			bestDistance = distance;
			best = (bone.Name, frame);
		}
		return best;
	}

	// Sticks to the nearest bone and rides the body's animation.
	// The host hit where the shooter saw the unit, so the point is moved onto the body as it is now.
	// Stuck, a "detonation maximum time if attached" timer runs at its upper bound's rate, keeping its progress.
	// A new needle restarts the timers of the newest needles already in the unit, up to the super combine count.
	// Older ones keep counting, so a long stream still goes off while it's fired.
	void Attach( Projectile p, in UnitHit hit ) {
		var target = hit.Unit;
		var tested = hit.TestedRoot;
		var root = target.Root;
		var local = tested.Rotation.Inverse * (p.Position - tested.Position);
		p.Position = root.Position + root.Rotation * local;
		p.AttachedTo = target;
		p.AttachAge = p.Age;
		p.AttachOffset = local;
		p.AttachLocal = new Transform( local, root.Rotation.Inverse * p.Orientation );
		p.Velocity = Vector3.Zero;
		p.SpinRate = 0;

		if ( NearestBone( target.Renderer, p.Position ) is { } bone ) {
			p.AttachBone = bone.Name;
			p.AttachLocal = new Transform( bone.Frame.PointToLocal( p.Position ), bone.Frame.Rotation.Inverse * p.Orientation );
		}

		if ( p.Tag.HasFlag( "detonation maximum time if attached" ) && p.Tag.TimerMaximum > 0 )
			p.TimerRate = RatePerTick( p.Tag.TimerMaximum );

		p.AttachOrder = ++_attaches;

		if ( p.Tag.HasFlag( "has super combining explosion" ) ) {
			var count = p.Tag.SuperDetonationProjectileCount;
			var restarted = 0;
			foreach ( var other in StuckIn( target, p ) ) {
				other.Timer = 0;
				other.Armed = 0;
				if ( count > 0 && ++restarted >= count ) break;
			}
		}
	}

	long _attaches;

	// Takes it off the unit it stuck to, its timer back at its unattached rate.
	void Detach( Projectile p ) {
		p.AttachedTo = null;
		p.AttachBone = null;
		if ( p.Tag.HasFlag( "detonation maximum time if attached" ) )
			p.TimerRate = RatePerTick( p.Tag.Timer?.FirstOrDefault() ?? 0 );
	}

	// A projectile that attaches to a corpse rides its nearest bone as one stuck in its Spartan before death would.
	// Without its Spartan, or a posed body, it sticks where it hit.
	void AttachToCorpse( Projectile p, SpartanCorpse corpse, Vector3 point, Vector3 normal ) {
		if ( Halo2Unit.Find( corpse.Owner ) is not { } owner || NearestBone( corpse.Renderer, p.Position ) is not { } bone ) {
			StickToWorld( p, point, normal );
			return;
		}
		p.AttachedTo = owner;
		p.SawDeath = true;
		p.CorpseDeath = corpse.Death;
		p.Corpse = corpse;
		p.AttachBone = bone.Name;
		p.AttachLocal = new Transform( bone.Frame.PointToLocal( p.Position ), bone.Frame.Rotation.Inverse * p.Orientation );
		p.Velocity = Vector3.Zero;
		p.SpinRate = 0;
		if ( p.Tag.HasFlag( "detonation maximum time if attached" ) && p.Tag.TimerMaximum > 0 )
			p.TimerRate = RatePerTick( p.Tag.TimerMaximum );
		p.AttachOrder = ++_attaches;
	}

	// The same kind of projectile stuck in the unit, not counting this one, newest first.
	IEnumerable<Projectile> StuckIn( Halo2Unit target, Projectile p ) =>
		_projectiles.Where( x => x != p && !x.Dead && x.AttachedTo == target && x.Corpse == p.Corpse && x.Launch.Projectile == p.Launch.Projectile )
			.OrderByDescending( x => x.AttachOrder );

	// More of a super combining projectile stuck in a biped than its count, this one included, super combine.
	// The oldest that many are used up and this one goes off at their centre, with the super detonation and damage.
	// The newer ones stay, their timer and arming progress cut to a random fraction.
	// Attached damage goes to the unit it's stuck in, and the area damage at the detonation goes to everything around.
	void Detonate( Projectile p ) {
		if ( p.Dead ) return;
		p.Dead = true;
		var tag = p.Tag;

		var super = false;
		var count = tag.SuperDetonationProjectileCount;
		if ( tag.HasFlag( "has super combining explosion" ) && p.AttachedTo is { } target && count > 0 ) {
			var stuck = StuckIn( target, p ).Append( p ).OrderByDescending( x => x.AttachOrder ).ToList();
			if ( stuck.Count > count ) {
				super = true;
				var centre = Vector3.Zero;
				for ( var i = 0; i < stuck.Count; i++ ) {
					var needle = stuck[i];
					if ( stuck.Count - i > count ) {
						needle.Timer *= needle.Random.Float( 0, 1 );
						needle.Armed *= needle.Random.Float( 0, 1 );
						continue;
					}
					needle.Dead = true;
					centre += needle.Position / count;
				}
				p.Position = centre;
			}
		}

		var forward = p.Forward;
		var effect = super && tag.SuperDetonation is not null ? HaloEffects.Role( p.Launch.Projectile, "super_detonation" ) : DetonationEffect( p );
		var damage = super ? tag.SuperDetonationDamage : tag.DetonationDamage;
		var attachedDamage = super ? tag.SuperAttachedDetonationDamage : tag.AttachedDetonationDamage;

		if ( !p.Authoritative ) {
			// Harmless detonations play locally; damaging ones come from the host.
			if ( string.IsNullOrEmpty( damage ) && !p.Launch.Hidden ) {
				if ( effect is not null ) HaloEffects.Play( effect, p.Position, forward );
				HaloSounds.Play( tag.DetonationSound, p.Position );
			}
			return;
		}

		if ( attachedDamage is not null && p.AttachedTo.IsValid() && !p.SawDeath ) {
			p.AttachedTo.TakeDamage( new DamageEvent {
				DamageEffect = attachedDamage,
				Scale = p.DamageScale,
				Position = p.Position,
				Direction = forward,
				AttackerId = p.Launch.Owner,
				WeaponName = p.Launch.Weapon,
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
			} );
		}

		HaloCombat.Detonate( Scene, effect, tag.DetonationSound, damage, p.Position, forward, p.Launch.Owner, p.Launch.Weapon, p.Rewind, p.Launch.Key );
	}

	// In the air, the airborne detonation effect; near the ground (its material effect radius) or stuck, the ground one.
	// Null without either.
	string DetonationEffect( Projectile p ) {
		var tag = p.Tag;
		var ground = tag.MaterialEffectRadius == 0 || p.Stuck || p.AttachedTo is not null || p.AtRest
			|| WorldTrace( p.Position, p.Position + Vector3.Down * HaloUnits.ToInches( tag.MaterialEffectRadius ) ).Hit;
		var role = (ground ? tag.DetonationEffectGround : null) is not null ? "detonation_ground" : tag.DetonationEffectAirborne is not null ? "detonation_airborne" : null;
		return role is null ? null : HaloEffects.Role( p.Launch.Projectile, role );
	}

	// The host's detonation arrived before our copy's: it goes off here too, using up the same super combined needles.
	public void HostDetonated( ProjectileKey key ) {
		if ( !key.Valid ) return;
		var copy = _projectiles.FirstOrDefault( p => !p.Authoritative && !p.Dead && p.Launch.Key == key );
		if ( copy is not null ) Detonate( copy );
	}
}

// How far a projectile is through its end: flying, due to detonate (once armed), or gone without a detonation.
public enum ProjectileState : byte { Flying, Detonate, Delete }

public sealed class Projectile {
	public ProjectileLaunch Launch;
	// Seeded from the launch.
	public HaloRandom Random;
	// Client: when the host last listed its copy of this one.
	public float ListedAt;
	public ProjectileTag Tag;
	public Vector3 Position;
	public Vector3 Velocity;
	public bool Authoritative;
	public RewindContext Rewind;
	public Halo2Unit Owner;
	public Halo2Unit Target;
	// Set while halo_showimpacts is on.
	public ImpactTrack Impact;

	// Null unless the local player fired it.
	public Halo2Unit LocalPlayerOwner => Owner.IsValid() && Owner.IsLocalPlayer ? Owner : null;

	public float Age;
	// World units flown.
	public float Distance;
	// The timer's and arming's progress to 1, and their steps a tick.
	public float Timer;
	public float TimerRate;
	public bool TimerRunning;
	public float Armed;
	public float ArmRate;
	// With no arming time it's armed whatever its arming progress.
	public bool IsArmed => ArmRate <= 0 || Armed >= 1;
	public ProjectileState State;
	public bool Dead;
	public bool Stuck;
	public Vector3 StuckNormal = Vector3.Up;
	public int Bounces;
	public bool HitAnything;
	// Seconds into its last update that it last hit something, null if it hit nothing then.
	public float? CollisionTime;
	public bool BouncedOffFloor;
	public bool AtRest;

	// Its speed in wu/s as it accelerates, the distance flown since launch for that, and the player speed scale.
	public float Speed;
	public float AccelerationDistance;
	public float SpeedScale = 1;

	// The impact damage's scale, 0 after a dud bounce.
	public float DamageScale = 1;

	// Radians per second.
	public float GuidedAngularVelocity;

	// The unit it overpenetrated, which it then passes through.
	public Halo2Unit Ignore;

	// Along its flight if oriented along velocity, otherwise its launch direction turned by its spin.
	public Rotation Orientation = Rotation.Identity;

	// The state before the last tick, for drawing between ticks.
	public Vector3 PreviousPosition;
	public Rotation PreviousOrientation = Rotation.Identity;

	// The detonation effect goes off along this, and acceleration pushes along it.
	public Vector3 Forward => Orientation.Forward;

	// Radians per second about SpinAxis.
	public Vector3 SpinAxis;
	public float SpinRate;

	// The global material it last hit, -1 before any hit.
	public int HitMaterial = -1;

	public Halo2Unit AttachedTo;
	// Higher for a later stick.
	public long AttachOrder;
	// Its age when it stuck to a unit.
	public float AttachAge;
	// Offset from the Spartan's root, for when there's no posed body.
	public Vector3 AttachOffset;

	// Null when not riding a bone.
	public string AttachBone;

	// Placement in the bone's or root's space.
	public Transform AttachLocal = Transform.Zero;

	// The Spartan has died since it stuck, so it never follows them into their next life; it rides that death's corpse.
	public bool SawDeath;
	public int CorpseDeath;
	public SpartanCorpse Corpse;

	// The corpse an overpenetrating shot has just passed through.
	public SpartanCorpse IgnoreCorpse;

	GameObject _visual;
	bool _behindViewModel;

	// The prefab's root renderer, or ours.
	ModelRenderer _model;

	// Its attachments' parts and lights, scaled by its object functions.
	ObjectAttachments _attachments;
	ObjectFunctions _functions;

	// Effects carried along, like "detonation started", with their offsets on the projectile.
	readonly List<(GameObject Effect, Vector3 Offset, Rotation Rotation)> _carried = new();

	// The view model's reach from the camera: one world unit, where Halo's sit within about 0.3.
	static readonly float ViewModelReach = HaloUnits.ToInches( 1 );

	public void CreateVisual( Scene scene ) {
		// "__projectile" is the whole projectile; "__attachments" is the same without the model.
		var stem = HaloEffects.WithoutExtension( Launch.Projectile );
		var whole = HaloEffects.Prefab( $"{stem}__projectile" );
		var attachments = whole is null ? HaloEffects.Prefab( $"{stem}__attachments" ) : null;
		var model = whole is null ? HaloResources.LoadModel( Tag.Model ) : null;
		DrawFromBarrel( scene );
		CreateContrails( scene, whole ?? attachments );

		// Halo draws nothing for a projectile without a model or attachments.
		if ( whole is null && model is null && attachments is null ) return;

		_visual = scene.CreateLocalObject( "projectile" );
		_visual.Tags.Add( "projectile" );
		_visual.WorldTransform = new Transform( Drawn( Position ), Orientation );
		_behindViewModel = LocalPlayerOwner is not null && SpartanView.LocalViewModelShown;

		if ( whole is not null || attachments is not null ) {
			// Reuse the in-hand grenade so its glow and hum carry on, as Halo's is the same object.
			var go = Launch.HeldVisual;
			if ( go.IsValid() ) {
				go.SetParent( _visual, false );
				go.LocalPosition = Vector3.Zero;
				go.LocalRotation = Rotation.Identity;
				// Thrown during the tick, so it must leave the hand at once rather than ease over.
				go.Transform.ClearInterpolation();
				go.GetComponent<Halo2HiddenRenderers>()?.Show();
			}
			else {
				go = SpawnPrefab( whole ?? attachments, Position, Orientation, _visual, this );
			}

			if ( go.IsValid() ) {
				_attachments = ObjectAttachments.ForPrefab( go, Launch.Projectile, lights: !Launch.HeldVisual.IsValid() );
				_functions = new ObjectFunctions( Tag.Functions, Input, Game.Random.Float() );
			}
			if ( go.IsValid() && whole is not null )
				_model = go.GetComponent<ModelRenderer>();
		}

		if ( model is not null ) {
			var renderer = _visual.AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_model = renderer;
		}

		ShowRenderers( !_behindViewModel );
	}

	// Spawns a projectile prefab with Halo's sound limits and no shadow.
	// Its contrails are removed because CreateContrails draws them as Halo does.
	static GameObject SpawnPrefab( PrefabFile prefab, Vector3 position, Rotation rotation, GameObject parent, object source ) {
		var go = HaloEffects.SpawnPrefab( prefab, position, rotation.Forward, parent, source );
		if ( !go.IsValid() ) return null;

		go.WorldRotation = rotation;
		go.Transform.ClearInterpolation();
		ContrailAttachments.Strip( go );
		if ( go.GetComponent<ModelRenderer>() is { } model )
			model.RenderType = ModelRenderer.ShadowRenderType.Off;
		return go;
	}

	// The grenade on the "left_hand" marker during a throw, its glow, light and hum already going.
	// Pass it on as ProjectileLaunch.HeldVisual, or destroy it.
	// In first person it isn't drawn, since Halo doesn't draw the camera's unit or what's attached to it.
	public static GameObject CreateHeld( string projectile, Halo2Unit thrower ) {
		if ( !thrower.IsValid() ) return null;
		var hand = thrower.LeftHand;
		var prefab = HaloEffects.Prefab( $"{HaloEffects.WithoutExtension( projectile )}__projectile" );
		if ( prefab is null || !hand.IsValid() ) return null;

		var go = SpawnPrefab( prefab, hand.WorldPosition, thrower.EyeAngles.ToRotation(), hand, null );
		if ( !go.IsValid() ) return null;
		ObjectAttachments.ForPrefab( go, projectile, lights: true );

		if ( FirstPersonUnit( thrower ) ) {
			var hidden = go.AddComponent<Halo2HiddenRenderers>();
			foreach ( var renderer in go.GetComponentsInChildren<Renderer>( true ).Where( x => x.Enabled ).ToList() ) {
				renderer.Enabled = false;
				hidden.Renderers.Add( renderer );
			}
		}
		return go;
	}

	// The local player's unit in first person, which Halo doesn't draw along with anything attached to it.
	static bool FirstPersonUnit( Halo2Unit unit ) =>
		unit.IsValid() && unit.IsLocalPlayer && unit.IsAlive && !SpartanView.ThirdPerson;

	// Toggles renderers only; lights and sounds keep going.
	void ShowRenderers( bool show ) {
		if ( show ) {
			foreach ( var renderer in _hidden )
				if ( renderer.IsValid() ) renderer.Enabled = true;
			_hidden.Clear();
			return;
		}

		if ( !_visual.IsValid() ) return;
		foreach ( var renderer in _visual.GetComponentsInChildren<Renderer>( true ) ) {
			if ( !renderer.Enabled ) continue;
			renderer.Enabled = false;
			_hidden.Add( renderer );
		}
	}

	readonly List<Renderer> _hidden = new();

	// Keeps an effect at its current offset on the projectile until the projectile is gone.
	public void Carry( GameObject effect ) {
		if ( !effect.IsValid() ) return;
		var inverse = Orientation.Inverse;
		_carried.Add( (effect, inverse * (effect.WorldPosition - Position), inverse * effect.WorldRotation) );
	}

	void UpdateCarried( Vector3 position, Rotation orientation ) {
		for ( var i = _carried.Count - 1; i >= 0; i-- ) {
			var (effect, offset, rotation) = _carried[i];
			if ( !effect.IsValid() ) {
				_carried.RemoveAt( i );
				continue;
			}
			effect.WorldPosition = position + orientation * offset;
			effect.WorldRotation = orientation * rotation;
		}
	}

	// Each contrail starts where it was fired from. Non-tracer shots skip the tracer.
	void CreateContrails( Scene scene, PrefabFile prefab ) {
		var system = ContrailSystem.Get( scene );
		if ( system is null ) return;

		foreach ( var attachment in ContrailAttachments.For( Launch.Projectile, prefab ) ) {
			if ( attachment.Tracer && !Launch.Tracer ) continue;
			var contrail = system.Create( ContrailDefinition.Get( attachment.Contrail ), ContrailMarker( attachment, Drawn( Position ) ), ContrailOrientation, Velocity );
			if ( contrail is not null ) _contrails.Add( (contrail, attachment) );
		}
		_contrailAge = Age;
	}

	// The shooting hand's first person barrel, or the held weapon's while zoomed or in third person.
	Vector3? LocalBarrel() {
		if ( LocalPlayerOwner is not { } owner || Launch.Thrown ) return null;
		if ( !SpartanView.ThirdPerson && SpartanView.LocalMuzzle( Launch.Key.Hand ) is { } muzzle ) return muzzle.Position;
		return owner.HeldMuzzle;
	}

	// Our own shot flies along the aim line but is drawn from the barrel.
	// The offset shrinks to zero where the aim line meets the level, so shot and trail run straight from the gun.
	void DrawFromBarrel( Scene scene ) {
		if ( LocalBarrel() is not { } barrel || Velocity.LengthSquared <= 1 ) return;

		var range = HaloUnits.ToInches( Tag.MaximumRange > 0 ? Tag.MaximumRange : 1000 );
		var level = ProjectileSimulation.Get( scene )?.WorldTrace( Position, Position + Velocity.Normal * range );
		_drawnFrom = Position;
		_drawnOffset = barrel - Position;
		_drawnReach = level is { Hit: true, StartedSolid: false } hit ? hit.Distance : range;
	}

	Vector3 _drawnFrom;
	Vector3 _drawnOffset;
	float _drawnReach;

	Vector3 Drawn( Vector3 position ) {
		if ( _drawnReach <= 0 || HitMaterial >= 0 || Stuck || AttachedTo is not null ) return position;
		return position + _drawnOffset * MathF.Max( 0, 1 - position.Distance( _drawnFrom ) / _drawnReach );
	}

	readonly List<(Contrail Contrail, ContrailAttachment Attachment)> _contrails = new();
	float _contrailAge;

	Rotation ContrailOrientation => Velocity.LengthSquared > 1 ? Rotation.LookAt( Velocity.Normal ) : Rotation.Identity;

	Vector3 ContrailMarker( ContrailAttachment attachment, Vector3 position ) => position + ContrailOrientation * attachment.Position;

	// Lays contrail points due since the last update, up to where the projectile is drawn now.
	void AdvanceContrails( float alpha, Vector3 position ) {
		var age = Age - (1 - alpha) * HaloUnits.TickInterval;
		var dt = age - _contrailAge;
		if ( dt <= 0 ) return;
		_contrailAge = age;

		foreach ( var (contrail, attachment) in _contrails )
			contrail.Advance( dt, ContrailMarker( attachment, Drawn( position ) ), ContrailOrientation, Velocity );
	}

	// A hit in its last update lays the tracer's points due by then, up to the hit, so the trail reaches it.
	// Then the contrails are orphaned and stay until their points expire.
	void ReleaseContrails() {
		foreach ( var (contrail, attachment) in _contrails ) {
			if ( attachment.Tracer && CollisionTime is { } time )
				contrail.OwnerCollision( time, ContrailMarker( attachment, Drawn( Position ) ), ContrailOrientation, Velocity );
			contrail.Orphan();
		}
		_contrails.Clear();
	}

	// Draws it alpha of the way through the last tick, as Spartans are drawn.
	public void UpdateVisual( float alpha ) {
		// Stuck to a body, it follows the bone as drawn this frame, not as of the last tick.
		if ( AttachBone is not null && AttachedTo is not null ) {
			var body = SawDeath ? Corpse?.Renderer : AttachedTo.IsValid() && AttachedTo.IsAlive ? AttachedTo.Renderer : null;
			if ( body.IsValid() && body.TryGetBoneTransform( AttachBone, out var frame ) ) {
				Position = PreviousPosition = frame.PointToWorld( AttachLocal.Position );
				Orientation = PreviousOrientation = frame.Rotation * AttachLocal.Rotation;
			}
		}

		var position = PreviousPosition.LerpTo( Position, alpha );
		var orientation = Rotation.Slerp( PreviousOrientation, Orientation, alpha );

		AdvanceContrails( alpha, position );
		UpdateCarried( position, orientation );
		if ( !_visual.IsValid() ) return;
		_attachments?.Update( _functions );
		ShaderFunctions.Apply( _visual, HaloResources.ModelPath( Tag.Model ), _functions );

		// Thrown or stuck projectiles use their orientation; the rest point along their flight.
		_visual.WorldPosition = Drawn( position );
		if ( Launch.Thrown || Stuck || AttachedTo is not null || Velocity.LengthSquared <= 1 )
			_visual.WorldRotation = orientation;
		else
			_visual.WorldRotation = Rotation.LookAt( Velocity.Normal );

		// Halo draws the view model over the world, so our own shots stay hidden behind it until clear of it.
		// Here they're depth tested, so we hide their renderers instead while lights and sounds keep going.
		// Once it hits, sticks or stops it's in front of the gun and shows however near it is.
		if ( _behindViewModel ) {
			var camera = _visual.Scene.Camera;
			var leftTheEye = HitMaterial >= 0 || Stuck || AttachedTo is not null || Velocity.LengthSquared < 1;
			if ( leftTheEye || !SpartanView.LocalViewModelShown || !camera.IsValid() || position.Distance( camera.WorldPosition ) > ViewModelReach ) {
				_behindViewModel = false;
				ShowRenderers( true );
			}
		}

		// Stuck to the first person local player, the model is hidden as Halo hides it; glow, light and sound go on.
		if ( _model.IsValid() && !_behindViewModel )
			_model.Enabled = !(AttachedTo is { } carrier && !SawDeath && FirstPersonUnit( carrier ));
	}

	// Halo 2's projectile input: how far through its acceleration range it has flown, 1 without one.
	float? Input( string name ) {
		if ( name != "acceleration_range" ) return null;
		var range = Tag.AccelerationRange;
		if ( Tag.InitialVelocity == 0 || Tag.FinalVelocity == 0 || range is not { Length: > 1 } || range[1] <= range[0] ) return 1;
		return (AccelerationDistance - SpeedScale * range[0]) / (range[1] - range[0]);
	}

	// Model, effects, light volumes and sounds go at once; the effects' loose particles live out.
	// Contrails are left to fade out over their points' lifetimes.
	public void DestroyVisual() {
		ReleaseContrails();
		_carried.Clear();
		if ( _visual.IsValid() ) _visual.Destroy();
		_visual = null;
	}
}

// A held grenade's renderers hidden in first person, shown again once it's thrown.
public sealed class Halo2HiddenRenderers : Component {
	public List<Renderer> Renderers { get; } = new();

	public void Show() {
		foreach ( var renderer in Renderers )
			if ( renderer.IsValid() ) renderer.Enabled = true;
		Renderers.Clear();
		Destroy();
	}
}
