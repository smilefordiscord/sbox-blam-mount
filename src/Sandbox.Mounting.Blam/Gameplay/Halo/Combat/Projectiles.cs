using System;
using Halo.Data;
using Halo.Combat;

namespace Halo.Combat;

// Identifies a projectile the same on every machine: shooter id, firing tick, and index within the shot.
// The host's corrections use it to find each machine's copy.
public readonly record struct ProjectileKey( int Shooter, int Tick, int Index ) {
	public bool Valid => Tick != 0;

	public ProjectileKey Nth( int n ) => this with { Index = Index + n };

	// Index ranges: hand weapon trigger pellets, vehicle gun pellets, and a grenade.
	public static ProjectileKey Shot( int shooter, int tick, int trigger ) => new( shooter, tick, trigger * 32 );
	public static ProjectileKey Gun( int shooter, int tick, int trigger ) => new( shooter, tick, 64 + trigger * 32 );
	public static ProjectileKey Grenade( int shooter, int tick ) => new( shooter, tick, 128 );
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
	// The vehicle it was fired from, which it passes through on its way out.
	public Guid Vehicle;
	// A thrown grenade, which also starts spinning.
	public bool Thrown;
	// Simulated but not seen or heard, for the host's copy of a shot the self-test client already shows.
	public bool Hidden;
	// The grenade's in-hand visual, carried on so its glow and hum continue as in Halo. Local only, never sent.
	public GameObject HeldVisual;
	// Seeds fuse, spin and bounces the same on every machine.
	public int Seed;
	public ProjectileKey Key;
}

// Halo projectiles: swept movement with gravity, air deceleration, homing, fuses and per-material responses.
// Halo has no hitscan; bullets are just very fast projectiles, and so are ours.
public sealed partial class ProjectileSimulation : GameObjectSystem<ProjectileSimulation> {
	public const int SupercombineNeedles = 7;
	const float MaxLifetime = 12f;

	readonly List<Projectile> _projectiles = new();

	public IReadOnlyList<Projectile> Active => _projectiles;

	public ProjectileSimulation( Scene scene ) : base( scene ) {
		Listen( Stage.StartUpdate, 10, Tick, "Halo.Projectiles" );
		Listen( Stage.FinishUpdate, 10, UpdateVisuals, "Halo.ProjectileVisuals" );
	}

	public Projectile Spawn( ProjectileLaunch launch, bool authoritative ) {
		using var perf = Halo.HaloPerf.Measure( "Projectiles.Spawn" );
		var tag = HaloDataStore.Game?.Projectile( launch.Projectile );
		if ( tag is null ) return null;

		var p = new Projectile {
			Launch = launch,
			Tag = tag,
			Position = launch.Origin,
			Velocity = launch.Velocity,
			Authoritative = authoritative,
			Owner = HaloUnitDirectory.Find( launch.Owner ),
			Target = HaloUnitDirectory.Find( launch.Target ),
			Random = new HaloRandom( launch.Seed ),
			Orientation = Rotation.LookAt( launch.Velocity.LengthSquared > 0.0001f ? launch.Velocity.Normal : Vector3.Forward ),
		};
		p.PreviousPosition = p.Position;
		p.PreviousOrientation = p.Orientation;

		// A thrown grenade spins about a random axis, up to pi/2 radians a tick per world unit a tick of push.
		if ( launch.Thrown ) {
			var push = HaloUnits.ToWorldUnits( launch.Velocity.Length ) / HaloUnits.TicksPerSecond;
			p.SpinAxis = p.Random.Direction();
			p.SpinRate = push * p.Random.Float( 0, 1 ) * (MathF.PI / 2) * HaloUnits.TicksPerSecond;
		}

		// A rider's shots start inside their vehicle, which they pass through on the way out.
		p.SourceVehicle = launch.Vehicle != Guid.Empty ? HaloUnitDirectory.Find( launch.Vehicle ) : null;
		p.Ignore = p.Owner;

		// The fuse is a random time within the timer bounds, or the lower bound for either detonation-time flag.
		// "detonation max time if attached" switches to the upper bound once attached.
		// "minimum unattached detonation time" is why flamethrower flames burn out after 1.3 s unless stuck.
		var minimum = tag.HasFlag( "detonation max time if attached" ) || tag.HasFlag( "minimum unattached detonation time" );
		var timer = tag.Timer is { Length: > 1 } t ? (minimum ? t[0] : p.Random.Float( t[0], t[1] )) : 0;
		p.FuseLength = timer;
		p.Fuse = timer;
		p.FuseRunning = timer > 0 && tag.DetonationTimerStarts == "immediately";
		p.Arming = tag.ArmingTime;

		if ( !launch.Hidden ) p.CreateVisual( Scene );
		_projectiles.Add( p );
		return p;
	}

	// The local living player, who hears flyby sounds.
	PlayerController _listener;

	// Once per Halo tick, as Halo updates its projectiles, so they're drawn between their last two ticks.
	void Tick() {
		for ( var i = 0; i < HaloTick.Advanced; i++ ) Step();
	}

	void Step() {
		using var perf = Halo.HaloPerf.Measure( "Projectiles.Step" );
		var dt = HaloUnits.TickInterval;
		_listener = PlayerController.FindLocalPlayer() is { Health: > 0 } local ? local : null;
		for ( var i = _projectiles.Count - 1; i >= 0; i-- ) {
			var p = _projectiles[i];
			p.Age += dt;
			if ( !p.Dead ) Simulate( p, dt );
			if ( p.Dead || p.Age > MaxLifetime ) {
				p.DestroyVisual();
				_projectiles.RemoveAt( i );
			}
		}
	}

	void UpdateVisuals() {
		using var perf = Halo.HaloPerf.Measure( "Projectiles.Visuals" );
		var alpha = HaloTick.Fraction;
		foreach ( var p in _projectiles )
			p.UpdateVisual( alpha );
	}

	// A detonation held back while unarmed goes off at the end of the first armed update.
	void Simulate( Projectile p, float dt ) {
		p.PreviousPosition = p.Position;
		p.PreviousOrientation = p.Orientation;
		Update( p, dt );
		if ( !p.Dead && p.DetonationPending && p.Arming <= 0 )
			Detonate( p, p.Position );
	}

	void Update( Projectile p, float dt ) {
		var tag = p.Tag;

		if ( p.Arming > 0 ) p.Arming -= dt;

		// Once counting down, later hits no longer play the "detonation started" effect.
		if ( p.FuseRunning ) p.CountingDown = true;

		FollowAttachment( p );
		if ( p.Dead ) return;

		if ( p.FuseRunning ) {
			p.Fuse -= dt;
			if ( p.Fuse <= 0 ) {
				RequestDetonate( p );
				if ( p.Dead ) return;
			}
		}

		if ( p.Stuck || p.AttachedTo is not null ) return;

		// A thrown grenade tumbles by its angular velocity while it moves.
		if ( p.SpinRate > 0 && !tag.HasFlag( "oriented along velocity" ) && p.Velocity.LengthSquared > 1 )
			p.Orientation = (Rotation.FromAxis( p.SpinAxis, (p.SpinRate * dt).RadianToDegree() ) * p.Orientation).Normal;

		var wuSpeed = HaloUnits.ToWorldUnits( p.Velocity.Length );

		// Needles and overcharged plasma steer at the guided angular velocity toward the target as the shooter saw it.
		if ( p.Target.IsValid() && p.Target.IsAlive && tag.GuidedAngularVelocity > 0 && p.Velocity.LengthSquared > 1 ) {
			var aim = p.Target.Center;
			var to = (aim - p.Position).Normal;
			var dir = p.Velocity.Normal;
			if ( Vector3.Dot( to, dir ) > 0 ) {
				var maxTurn = tag.GuidedAngularVelocity.DegreeToRadian() * dt;
				var angle = MathF.Acos( Math.Clamp( Vector3.Dot( dir, to ), -1, 1 ) );
				var t = angle > 0.0001f ? MathF.Min( 1, maxTurn / angle ) : 1;
				dir = Vector3.Lerp( dir, to, t ).Normal;
				p.Velocity = dir * p.Velocity.Length;
			}
		}

		// Deceleration from initial to final velocity across the air damage range.
		var vi = tag.InitialVelocity;
		var vf = tag.FinalVelocity;
		var range = tag.AirDamageRange is { Length: > 1 } r ? r : null;
		if ( range is not null && vi > vf && range[1] > range[0] && HaloUnits.ToWorldUnits( p.Travelled ) >= range[0] ) {
			var viS = vi * HaloUnits.TicksPerSecond;
			var vfS = vf * HaloUnits.TicksPerSecond;
			var decel = (viS * viS - vfS * vfS) / (2 * (range[1] - range[0]));
			var newSpeed = MathF.Max( vfS * 0.99f, wuSpeed - decel * dt );
			if ( wuSpeed > 0.0001f )
				p.Velocity = p.Velocity * (newSpeed / wuSpeed);
		}

		// The move is tested a piece at a time. Each collision stops at the hit and refunds the unflown part's gravity.
		// A bounce carries on with the rest of the update, up to ten collisions, then it detonates.
		var gravity = HaloUnits.Gravity * tag.AirGravityScale;
		var remaining = dt;
		var flybyPlayed = false;
		Vector3 direction;
		for ( var collisions = 0; ; collisions++ ) {
			if ( collisions == MaximumCollisionsPerUpdate ) {
				RequestDetonate( p );
				return;
			}

			// It moves by its average velocity over the step.
			var newVelocity = p.Velocity.WithZ( p.Velocity.z - gravity * remaining );
			var move = p.Velocity.WithZ( p.Velocity.z - gravity * remaining * 0.5f ) * remaining;
			var start = p.Position;
			var length = move.Length;
			if ( length < 0.001f ) {
				p.Velocity = newVelocity;
				if ( collisions > 0 ) return;

				// At rest: grenades start their fuse here.
				if ( !p.FuseRunning && p.FuseLength > 0 ) p.FuseRunning = true;
				else if ( p.FuseLength <= 0 && tag.Effect is not null ) RequestDetonate( p );
				return;
			}

			direction = move / length;

			// The detonation effect goes off along this forward.
			if ( tag.HasFlag( "oriented along velocity" ) )
				p.Orientation = Rotation.LookAt( direction );

			// One trace meets units' hit boxes and everything else. The shooter is ignored until the first collision,
			// and the vehicle it was fired from for its first half second.
			var ignore = p.Ignore ?? (p.Age < 0.5f ? p.SourceVehicle : null);
			var hitUnit = UnitTrace.Ray( Scene, start, direction, length, ignore, out var unitHit, out var world ) && unitHit.Hit.Distance > 0;
			var hitWorld = !hitUnit && world.Hit && !world.StartedSolid;

			var travelled = hitUnit ? unitHit.Hit.Distance : hitWorld ? world.Distance : length;
			if ( !flybyPlayed ) flybyPlayed = Flyby( p, start, direction * travelled );

			if ( !hitUnit && !hitWorld ) {
				p.Position = start + move;
				p.Velocity = newVelocity;
				p.Travelled += length;
				break;
			}

			// The velocity at the hit only has gravity for the time flown.
			var flown = remaining * Math.Clamp( travelled / length, 0, 1 );
			remaining -= flown;
			p.Velocity = p.Velocity.WithZ( p.Velocity.z - gravity * flown );
			p.Travelled += travelled;
			p.Ignore = null;

			if ( hitUnit ) {
				p.Position = unitHit.Hit.Position;
				HitUnit( p, unitHit, direction );
			}
			else {
				p.Position = world.HitPosition;
				HitWorld( p, world, direction, HaloSurfaces.MaterialOf( world.Surface ) );
			}

			PlayDetonationStarted( p );

			// Only a bounce or overpenetration flies on.
			if ( p.Dead || p.Stuck || p.AttachedTo is not null || remaining <= 0 ) return;
		}

		if ( tag.MaximumRange > 0 && HaloUnits.ToWorldUnits( p.Travelled ) > tag.MaximumRange ) {
			if ( tag.Effect is not null ) RequestDetonate( p );
			else p.Dead = true;
			return;
		}

		if ( tag.MinimumVelocity > 0 && wuSpeed / HaloUnits.TicksPerSecond < tag.MinimumVelocity && p.Age > 0.1f )
			RequestDetonate( p );
	}

	// Plays the flyby sound at the nearest point of the move when it passes the listener within the sound's range.
	// The listener's projection must fall within the move, and the shooter hears nothing until the first collision.
	// Returns whether it played; it plays at most once per update.
	bool Flyby( Projectile p, Vector3 start, Vector3 movement ) {
		var sound = p.Tag.FlybySound;
		var listener = _listener;
		if ( string.IsNullOrEmpty( sound ) || listener is null || p.Launch.Hidden ) return false;
		if ( listener.GameObject == p.Owner?.GameObject && p.HitMaterial < 0 ) return false;

		var lengthSquared = movement.LengthSquared;
		if ( lengthSquared < 1e-6f ) return false;

		var maximumDistance = HaloSounds.MaximumDistance( sound );
		if ( maximumDistance <= 0 ) return false;

		var toListener = listener.EyeTransform.Position - start;
		var along = Vector3.Dot( toListener, movement );
		if ( along < 0 || along >= lengthSquared ) return false;

		var nearest = start + movement * (along / lengthSquared);
		var perpendicularWu = HaloUnits.ToWorldUnits( (start + toListener).Distance( nearest ) );
		if ( perpendicularWu >= maximumDistance ) return false;

		HaloSounds.Play( sound, nearest );
		return true;
	}

	// Indexed by material type. Spartans are cyborg energy shield (22) while shielded, cyborg armor (21) otherwise.
	static ProjectileMaterialResponseTag Response( ProjectileTag tag, int materialIndex ) {
		var list = tag.ProjectileMaterialResponse;
		if ( list is null || list.Count == 0 ) return null;
		return materialIndex >= 0 && materialIndex < list.Count ? list[materialIndex] : list[0];
	}

	// Speed into the surface in world units per tick, less up to the response's velocity noise.
	static float ImpactVelocity( Projectile p, Vector3 normal, ProjectileMaterialResponseTag r ) {
		var into = -Vector3.Dot( p.Velocity, normal );
		var noise = r?.VelocityNoise ?? 0;
		return HaloUnits.ToWorldUnits( into ) / HaloUnits.TicksPerSecond + (noise != 0 ? p.Random.Float( -noise, 0 ) : 0);
	}

	// Degrees from the surface, 0 grazing to 90 head-on, plus or minus the angular noise.
	static float ImpactAngle( Projectile p, Vector3 normal, ProjectileMaterialResponseTag r ) {
		var velocity = p.Velocity.LengthSquared > 0.0001f ? p.Velocity.Normal : -normal;
		var between = MathF.Acos( Math.Clamp( Vector3.Dot( velocity, normal ), -1, 1 ) ).RadianToDegree();
		var noise = r?.AngularNoise ?? 0;
		return between - 90 + (noise != 0 ? p.Random.Float( -noise, noise ) : 0);
	}

	// The potential response wins when set, when angle and velocity are within its bounds, and it isn't skipped.
	// An upper bound of 0 means any. "only against units" limits it to unit hits.
	// Otherwise the default response plays.
	static string PickResponse( Projectile p, ProjectileMaterialResponseTag r, bool againstUnit, float impactVelocity, float impactAngle, out string effect ) {
		effect = r?.DefaultEffect;
		if ( r is null ) return "disappear";

		var potential = r.PotentialResponse;
		if ( potential is not (null or "" or "disappear") ) {
			var onlyUnits = r.PotentialFlags?.Contains( "only against units" ) ?? false;
			var angleOk = r.PotentialBetween is not { Length: > 1 } b || b[1] == 0 || (impactAngle >= b[0] && impactAngle <= b[1]);
			var velocityOk = r.PotentialAnd is not { Length: > 1 } a || a[1] == 0 || (impactVelocity >= a[0] && impactVelocity <= a[1]);

			if ( angleOk && velocityOk && (!onlyUnits || againstUnit) && p.Random.Float() >= r.PotentialSkipFraction ) {
				effect = r.PotentialEffect;
				return potential;
			}
		}

		return r.DefaultResponse ?? "disappear";
	}

	// The response's effect only plays for impacts faster than this, in world units per tick.
	const float MinimumImpactEffectVelocity = 1f / 120f;

	void HitUnit( Projectile p, UnitHit hit, Vector3 direction ) {
		var tag = p.Tag;
		var target = hit.Unit;
		var incoming = p.Velocity;
		var speed = HaloUnits.ToWorldUnits( p.Velocity.Length ) / HaloUnits.TicksPerSecond;
		var material = target.HitMaterial( hit );

		if ( p.Authoritative && tag.ImpactDamage is not null ) {
			// Impact damage falls off from full at the initial velocity to the minimum at the final.
			var vi = tag.InitialVelocity;
			var vf = tag.FinalVelocity;
			var scale = vi > vf ? Math.Clamp( (speed - vf) / (vi - vf), 0, 1 ) : 1;

			var damage = target.TakeDamage( new DamageEvent {
				DamageEffect = tag.ImpactDamage,
				Scale = scale,
				Position = hit.Hit.Position,
				Direction = direction,
				MaterialName = hit.Hit.Material,
				AttackerId = p.Launch.Owner,
				WeaponName = p.Launch.Weapon,
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
				FromWeapon = true,
			} );

			// The response follows the material the damage recorded.
			if ( damage.MaterialType is { } recorded ) material = recorded;
		}

		var r = Response( tag, material );
		var impactVelocity = ImpactVelocity( p, hit.Hit.Normal, r );
		var response = PickResponse( p, r, true, impactVelocity, ImpactAngle( p, hit.Hit.Normal, r ), out var effect );
		p.HitMaterial = material;

		// Every machine plays the response's effect for a fast enough hit, whatever the response.
		// A detonation adds the projectile's own effect.
		if ( effect is not null && impactVelocity > MinimumImpactEffectVelocity && !p.Launch.Hidden )
			HaloEffects.Play( effect, hit.Hit.Position, hit.Hit.Normal, velocity: p.Velocity );

		switch ( response ) {
			case "attach":
				Attach( p, hit );
				break;
			case "detonate":
				p.Velocity = Vector3.Zero;
				p.StoppedAfterCollision = true;
				RequestDetonate( p );
				break;
			case "overpenetrate":
				// It flies on through the unit, slowed by the response's initial friction, and can't hit it again until its next collision.
				p.Velocity *= 1 - (r?.InitialFriction ?? 0);
				p.Ignore = target;
				break;
			case "reflect":
				// It stays where it hit, on the body's surface.
				Reflect( p, hit.Hit.Normal, r );
				break;
			default:
				p.Dead = true;
				break;
		}

		// A stuck projectile has already been moved onto the body as it is now.
		DetonationStarted( p, response, response == "attach" ? p.Position : hit.Hit.Position, hit.Hit.Normal, incoming );
	}

	// Impact damage to a Sandbox object that takes damage or a loose body it pushes, as Halo's projectiles hit every object.
	void DamageObject( Projectile p, SceneTraceResult tr, Vector3 direction ) {
		var tag = p.Tag;
		if ( !p.Authoritative || tag.ImpactDamage is null || !tr.GameObject.IsValid() ) return;
		if ( tr.GameObject.GetComponentInParent<Component.IDamageable>() is null && tr.GameObject.GetComponentInParent<Rigidbody>() is null ) return;

		var speed = HaloUnits.ToWorldUnits( p.Velocity.Length ) / HaloUnits.TicksPerSecond;
		var vi = tag.InitialVelocity;
		var vf = tag.FinalVelocity;
		var scale = vi > vf ? Math.Clamp( (speed - vf) / (vi - vf), 0, 1 ) : 1;
		SandboxUnit.Apply( tr.GameObject, new DamageEvent {
			DamageEffect = tag.ImpactDamage,
			Scale = scale,
			Position = tr.HitPosition,
			Direction = direction,
			AttackerId = p.Launch.Owner,
			WeaponName = p.Launch.Weapon,
			Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
			FromWeapon = true,
		} );
	}

	// againstUnit marks a vehicle hit, for "only against units" responses.
	void HitWorld( Projectile p, SceneTraceResult tr, Vector3 direction, int material = 0, bool againstUnit = false ) {
		var tag = p.Tag;
		var incoming = p.Velocity;
		var r = Response( tag, material );
		var impactVelocity = ImpactVelocity( p, tr.Normal, r );
		if ( !againstUnit ) DamageObject( p, tr, direction );
		var response = PickResponse( p, r, againstUnit, impactVelocity, ImpactAngle( p, tr.Normal, r ), out var effect );
		p.HitMaterial = material;

		// The effect plays whatever the response, so a detonating overcharged bolt shows impact and detonation.
		if ( effect is not null && impactVelocity > MinimumImpactEffectVelocity && !p.Launch.Hidden )
			HaloEffects.Play( effect, tr.HitPosition, tr.Normal, velocity: p.Velocity );

		switch ( response ) {
			case "detonate":
				p.Position = tr.HitPosition;
				p.Velocity = Vector3.Zero;
				p.StoppedAfterCollision = true;
				RequestDetonate( p );
				break;
			case "reflect":
				// Left at the hit point, kept on the open side of the surface as Halo does.
				p.Position = tr.HitPosition + tr.Normal * SurfaceSkin;
				Reflect( p, tr.Normal, r );
				break;
			case "attach":
				p.Stuck = true;
				p.Velocity = Vector3.Zero;
				p.SpinRate = 0;
				p.Position = tr.HitPosition;
				p.StuckNormal = tr.Normal;
				StartAttachedTimer( p );
				break;
			case "overpenetrate":
				p.Position = tr.HitPosition + direction * 2;
				break;
			default:
				p.Dead = true;
				break;
		}

		DetonationStarted( p, response, tr.HitPosition, tr.Normal, incoming );
	}

	// A hit that stops or sticks the projectile before its fuse counts down queues the "detonation started" effect.
	// It follows what the projectile is stuck to, like a plasma grenade's crackle, or stays on the level.
	// PlayDetonationStarted plays it once the collision is handled and any attachment is made.
	static void DetonationStarted( Projectile p, string response, Vector3 point, Vector3 normal, Vector3 incoming ) {
		if ( p.CountingDown || string.IsNullOrEmpty( p.Tag.DetonationStarted ) ) return;
		if ( !p.StoppedAfterCollision && response != "attach" ) return;
		p.DetonationStartedAt = (point, normal, incoming);
	}

	static void PlayDetonationStarted( Projectile p ) {
		if ( p.DetonationStartedAt is not { } at ) return;
		p.DetonationStartedAt = null;
		if ( p.Launch.Hidden ) return;

		var effect = HaloEffects.PlayObject( p.Tag.DetonationStarted, at.Point, at.Normal, velocity: at.Incoming, source: p );
		if ( effect.IsValid() && p.AttachedTo is not null && !p.Dead )
			p.Carry( effect );
	}

	const int MaximumCollisionsPerUpdate = 10;

	// Halo nudges hit points 1/4096 world units off the surface when they'd land in the wrong BSP leaf.
	// We can't check leaves, so it always applies; otherwise a grenade sliding down a steep face can trace through it.
	public static readonly float SurfaceSkin = HaloUnits.ToInches( 1f / 4096f );

	// Skips players, triggers, projectiles, debris and ragdolls.
	public SceneTraceResult WorldTrace( Vector3 start, Vector3 end ) => Scene.Trace.Ray( start, end )
		.WithoutTags( "player", "playercontroller", "trigger", "projectile", "debris", "ragdoll", "viewmodel" )
		.Run();

	// The normal part flips and keeps 1 - parallel friction; the surface part keeps 1 - perpendicular friction.
	// Noise then turns and rescales it, and below the minimum velocity it detonates.
	// Under 0.01 world units a tick it has stopped, which starts the fuse.
	// Stopped on ground with a normal over 0.3 up, it stays put, and without a timer it detonates.
	void Reflect( Projectile p, Vector3 normal, ProjectileMaterialResponseTag r ) {
		var v = p.Velocity;
		var parallel = normal * Vector3.Dot( v, normal );
		var perpendicular = v - parallel;
		v = perpendicular * (1 - (r?.PerpendicularFriction ?? 0)) - parallel * (1 - (r?.ParallelFriction ?? 0));

		// Turns by up to the noise about an axis square to the velocity and a random direction.
		var angularNoise = r?.AngularNoise ?? 0;
		if ( angularNoise != 0 && v.LengthSquared > 0 ) {
			var axis = Vector3.Cross( v, p.Random.Direction() );
			if ( axis.LengthSquared > 0 )
				v = Rotation.FromAxis( axis.Normal, p.Random.Float( 0, angularNoise ) ) * v;
		}

		// Velocity noise is in world units per tick.
		var velocityNoise = r?.VelocityNoise ?? 0;
		if ( velocityNoise != 0 && v.LengthSquared > 0 ) {
			var speed = v.Length + HaloUnits.PerTickToInchesPerSecond( p.Random.Float( -velocityNoise, velocityNoise ) );
			v = v.Normal * speed;
		}

		p.Velocity = v;
		p.Bounces++;

		var tag = p.Tag;
		var speedPerTick = HaloUnits.ToWorldUnits( v.Length ) / HaloUnits.TicksPerSecond;
		if ( speedPerTick < tag.MinimumVelocity )
			RequestDetonate( p );

		if ( speedPerTick * speedPerTick >= 0.0001f ) return;

		p.StoppedAfterCollision = true;
		if ( p.FuseLength > 0 ) p.FuseRunning = true;
		if ( normal.z <= 0.3f ) return;

		p.Stuck = true;
		p.StuckNormal = normal;
		p.Velocity = Vector3.Zero;
		if ( (tag.Timer is { Length: > 1 } t ? t[1] : 0) == 0 )
			RequestDetonate( p );
	}

	// Keeps a stuck projectile on its bone or vehicle. Halo's dead biped is the same object, so it stays on the corpse.
	// When that body goes away it detaches, arms and detonates at once.
	void FollowAttachment( Projectile p ) {
		if ( p.AttachedTo is { } target ) {
			SkinnedModelRenderer renderer = null;
			var parentGone = !target.IsValid();

			if ( p.AttachedCorpse is not null ) {
				// The corpse stays in the world after the player respawns.
				renderer = p.AttachedCorpse;
				parentGone = !renderer.IsValid();
			}
			else if ( !parentGone && !target.IsAlive ) {
				// Switch to the corpse once one exists from this death. A mission unit's corpse is its own body.
				p.SawDeath = true;
				if ( target.Corpse is { } corpse && corpse.IsValid() && corpse != p.EarlierCorpse )
					renderer = p.AttachedCorpse = corpse;
			}
			else if ( !parentGone && p.SawDeath ) {
				// Respawned before we saw a body to stay on.
				parentGone = true;
			}
			else if ( !parentGone ) {
				renderer = target.Renderer;
			}

			if ( parentGone ) {
				p.AttachedTo = null;
				p.AttachedCorpse = null;
				p.Stuck = true;
				p.Arming = 0;
				RequestDetonate( p );
				return;
			}

			if ( p.AttachBone is not null && renderer.IsValid() && renderer.TryGetBoneTransform( p.AttachBone, out var frame ) ) {
				p.Position = frame.PointToWorld( p.AttachLocal.Position );
				p.Orientation = frame.Rotation * p.AttachLocal.Rotation;
			}
			else if ( target.IsAlive && !p.SawDeath && p.AttachBone is null ) {
				var root = target.Root;
				p.Position = root.Position + root.Rotation * p.AttachOffset;
				p.Orientation = root.Rotation * p.AttachLocal.Rotation;
			}
			// A body that can't be posed right now leaves it where it was.
		}
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

	// Attaching starts the fuse. "detonation max time if attached" switches to the upper bound's rate, keeping progress.
	static void StartAttachedTimer( Projectile p ) {
		if ( p.FuseLength <= 0 ) return;

		if ( p.Tag.HasFlag( "detonation max time if attached" ) && p.Tag.Timer is { Length: > 1 } t && t[1] > 0 ) {
			var left = Math.Clamp( p.Fuse / p.FuseLength, 0, 1 );
			p.FuseLength = t[1];
			p.Fuse = left * t[1];
		}
		RandomAttachedTimer( p );
		p.FuseRunning = true;
	}

	// Once stuck, the rest of the fuse runs at the rate of a random time within the timer bounds.
	// A flamethrower flame burns 1.3 to 8 s on whatever it caught.
	static void RandomAttachedTimer( Projectile p ) {
		if ( !p.Tag.HasFlag( "random attached detonation time" ) || p.Tag.Timer is not { Length: > 1 } t || p.FuseLength <= 0 ) return;
		var length = p.Random.Float( t[0], t[1] );
		p.Fuse = p.Fuse / p.FuseLength * length;
		p.FuseLength = length;
	}

	// Sticks to the node hit and rides the body's animation, alive or dead.
	// That's the hit box's bone for a mission unit, or the nearest bone for a Spartan.
	// The host hit where the shooter saw the unit, so the point is moved onto the body as it is now.
	void Attach( Projectile p, in UnitHit hit ) {
		var target = hit.Unit;
		var tested = hit.TestedRoot;
		var root = target.Root;
		var local = tested.Rotation.Inverse * (p.Position - tested.Position);
		p.Position = root.Position + root.Rotation * local;
		p.AttachedTo = target;
		p.EarlierCorpse = target.Corpse;
		p.AttachOffset = local;
		p.AttachLocal = new Transform( local, root.Rotation.Inverse * p.Orientation );
		p.Velocity = Vector3.Zero;
		p.SpinRate = 0;

		var renderer = target.Renderer;
		if ( hit.Node is not null && renderer.IsValid() && renderer.TryGetBoneTransform( hit.Node, out var node ) ) {
			p.AttachBone = hit.Node;
			p.AttachLocal = new Transform( node.PointToLocal( p.Position ), node.Rotation.Inverse * p.Orientation );
		}
		else if ( NearestBone( renderer, p.Position ) is { } bone ) {
			p.AttachBone = bone.Name;
			p.AttachLocal = new Transform( bone.Frame.PointToLocal( p.Position ), bone.Frame.Rotation.Inverse * p.Orientation );
		}

		StartAttachedTimer( p );

		if ( p.Authoritative && p.Tag.AttachedDetonationDamage is not null )
			p.AttachedDamage = true;

		// A needle that sticks restarts the timers of the newest six already in the target, so they go off together.
		if ( p.Tag.HasFlag( "has super combining explosion" ) ) {
			var restarted = 0;
			for ( var i = _projectiles.Count - 1; i >= 0 && restarted < SupercombineNeedles - 1; i-- ) {
				var other = _projectiles[i];
				if ( other == p || other.Dead || other.SuperExploded || other.AttachedTo != target || other.Launch.Projectile != p.Launch.Projectile ) continue;
				other.Fuse = other.FuseLength;
				other.Arming = other.Tag.ArmingTime;
				restarted++;
			}
		}
	}

	// When a needle in a biped goes off with at least seven in it, it makes the super detonation at the biped's origin.
	// A campaign player's biped never supercombines. The oldest six left pop at random soon after; any newer start over.
	bool SuperCombines( Projectile p ) {
		var target = p.AttachedTo;
		if ( p.SuperExploded || p.Tag.SuperDetonation is null || !p.Tag.HasFlag( "has super combining explosion" ) || !target.IsValid() ) return false;

		var stuck = _projectiles.Where( x => x.AttachedTo == target && !x.SuperExploded && x.Launch.Projectile == p.Launch.Projectile && (x == p || !x.Dead) ).ToList();
		if ( stuck.Count < SupercombineNeedles ) return false;

		for ( var i = stuck.Count - 1; i >= 0; i-- ) {
			var needle = stuck[i];
			if ( i < SupercombineNeedles - 1 ) {
				needle.SuperExploded = true;
				needle.Fuse = needle.FuseLength - (needle.FuseLength - needle.Fuse) * needle.Random.Float( 0, 1 );
				needle.Arming = needle.Tag.ArmingTime - (needle.Tag.ArmingTime - needle.Arming) * needle.Random.Float( 0, 1 );
			}
			else {
				needle.Fuse = needle.FuseLength;
				needle.Arming = needle.Tag.ArmingTime;
			}
		}
		return true;
	}

	// An unarmed projectile holds the detonation and flies on until armed, so a frag never goes off before 1.5 s.
	void RequestDetonate( Projectile p ) {
		if ( p.Dead ) return;
		if ( p.Arming > 0 ) {
			p.DetonationPending = true;
			return;
		}
		Detonate( p, p.Position );
	}

	// The effect goes off along the projectile's forward, wherever flight or tumble left it, with "gravity" straight down.
	// The last hit material's detonation effect plays the same way.
	void Detonate( Projectile p, Vector3 position ) {
		if ( p.Dead ) return;
		p.Dead = true;

		// The super detonation's needle leaves the body, so it deals no attached damage.
		var combined = SuperCombines( p );
		if ( combined ) position = p.AttachedCorpse.IsValid() ? p.AttachedCorpse.WorldPosition : p.AttachedTo.Root.Position;
		p.Position = position;

		var forward = p.Forward;
		DetonationEffect( p, combined ? p.Tag.SuperDetonation : p.Tag.Effect, position, forward );

		// Attached detonation damage hits what it's stuck to directly, not as area damage.
		// A mission unit's body is the unit, dead or alive; a dead Spartan's player has moved on.
		if ( !combined && p.Authoritative && p.AttachedDamage && p.AttachedTo.IsValid() && !p.SawDeath ) {
			p.AttachedTo.TakeDamage( new DamageEvent {
				DamageEffect = p.Tag.AttachedDetonationDamage,
				Scale = 1,
				Position = position,
				Direction = forward,
				AttackerId = p.Launch.Owner,
				WeaponName = p.Launch.Weapon,
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
				FromWeapon = true,
			} );
		}

		if ( p.HitMaterial >= 0 )
			DetonationEffect( p, Response( p.Tag, p.HitMaterial )?.DetonationEffect, position, forward );
	}

	void DetonationEffect( Projectile p, string effect, Vector3 position, Vector3 forward ) {
		if ( effect is null ) return;

		if ( p.Authoritative ) {
			HaloCombat.Detonate( Scene, effect, position, forward, p.Launch.Owner, p.Launch.Weapon );
		}
		else if ( !EffectDamages( effect ) ) {
			// Harmless effects (needle pops) play locally; damaging ones come from the host.
			HaloEffects.Play( effect, position, forward );
		}
	}

	static bool EffectDamages( string effectPath ) {
		var effect = HaloDataStore.Game?.Effect( effectPath );
		return effect?.Parts.Any( x => x.IsDamage ) ?? false;
	}

	// Removes local copies near a point when the host's detonation arrives first.
	public void ClearNear( Vector3 position, float radius, Guid owner ) {
		foreach ( var p in _projectiles ) {
			if ( !p.Authoritative && p.Launch.Owner == owner && p.Tag.Effect is not null && p.Position.Distance( position ) < radius )
				p.Dead = true;
		}
	}
}

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
	public IHaloUnit Owner;
	public IHaloUnit Target;
	// The unit its moves pass through: its shooter until the first collision, or one it overpenetrated until the next.
	public IHaloUnit Ignore;

	// Null unless the local player fired it.
	public PlayerController LocalPlayerOwner => Owner is SandboxUnit { Player: { } owner } && owner.IsValid() && owner.IsLocalPlayer ? owner : null;

	public float Age;
	public float Travelled;
	public float Fuse;
	public float FuseLength;
	public bool FuseRunning;
	public float Arming;
	public bool Dead;
	public bool Stuck;
	public Vector3 StuckNormal;
	public int Bounces;

	// Along its flight if oriented along velocity, otherwise its launch direction turned by its spin.
	public Rotation Orientation = Rotation.Identity;

	// The state before the last tick, for drawing between ticks.
	public Vector3 PreviousPosition;
	public Rotation PreviousOrientation = Rotation.Identity;

	// The detonation effect goes off along this.
	public Vector3 Forward => Orientation.Forward;

	// Radians per second about SpinAxis.
	public Vector3 SpinAxis;
	public float SpinRate;

	public bool CountingDown;

	// A collision left it all but stopped.
	public bool StoppedAfterCollision;

	// Should have detonated but isn't armed yet.
	public bool DetonationPending;

	// Where a collision this update started its fuse, for the "detonation started" effect.
	public (Vector3 Point, Vector3 Normal, Vector3 Incoming)? DetonationStartedAt;

	// The material type it last hit, -1 before any hit.
	public int HitMaterial = -1;

	public IHaloUnit AttachedTo;
	// A needle left in a body by a super detonation, which can't make another.
	public bool SuperExploded;
	// Offset from the unit's root, for when there's no posed body.
	public Vector3 AttachOffset;
	public bool AttachedDamage;

	// Null when not riding a bone.
	public string AttachBone;

	// Placement in the bone's, vehicle's or root's space.
	public Transform AttachLocal = Transform.Zero;

	// The corpse it rides after death, and the corpse the unit already had when it stuck.
	public SkinnedModelRenderer AttachedCorpse;
	public SkinnedModelRenderer EarlierCorpse;

	// The unit has died since it stuck, so it never follows them into their next life.
	public bool SawDeath;

	// The vehicle it was fired from, which it passes through briefly.
	public IHaloUnit SourceVehicle;

	GameObject _visual;
	LineRenderer _tracer;
	Vector3 _tracerTail;
	bool _behindViewModel;

	// The prefab's root renderer, or ours.
	ModelRenderer _model;

	// Effects carried along, like "detonation started", with their offsets on the projectile.
	readonly List<(GameObject Effect, Vector3 Offset, Rotation Rotation)> _carried = new();

	// The view model's reach from the camera: one world unit, where Halo's sit within about 0.3.
	static readonly float ViewModelReach = HaloUnits.ToInches( 1 );

	public void CreateVisual( Scene scene ) {
		// "__projectile" is the whole projectile; "__attachments" is the same without the model.
		var stem = HaloEffects.WithoutExtension( Launch.Projectile );
		var whole = HaloEffects.PrefabsEnabled ? HaloEffects.Prefab( $"{stem}__projectile" ) : null;
		var attachments = whole is null && HaloEffects.PrefabsEnabled ? HaloEffects.Prefab( $"{stem}__attachments" ) : null;

		var model = whole is null ? HaloResources.LoadModel( Tag.Model ) : null;
		DrawFromBarrel( scene );
		CreateContrails( scene, whole ?? attachments );

		// Halo draws nothing for a projectile without a model or attachments, like pistol rounds.
		// The stand-in streak is only for playing without the mount's effects.
		if ( whole is null && model is null && attachments is null && (HaloEffects.PrefabsEnabled || !Launch.Tracer) ) return;

		_visual = scene.CreateLocalObject( "projectile" );
		_visual.Tags.Add( "projectile" );
		_visual.WorldTransform = new Transform( Drawn( Position ), Orientation );
		_behindViewModel = LocalPlayerOwner is not null && HaloView.LocalViewModelShown;

		if ( whole is not null || attachments is not null ) {
			// Reuse the in-hand grenade so its glow and hum carry on, as Halo's is the same object.
			var go = Launch.HeldVisual;
			if ( go.IsValid() ) {
				go.SetParent( _visual, false );
				go.LocalPosition = Vector3.Zero;
				go.LocalRotation = Rotation.Identity;
				// Thrown during the tick, so it must leave the hand at once rather than ease over.
				go.Transform.ClearInterpolation();
				go.GetComponent<HiddenRenderers>()?.Show();
			}
			else {
				go = SpawnPrefab( whole ?? attachments, Position, Orientation, _visual, this );
			}

			if ( go.IsValid() && whole is not null )
				_model = go.GetComponent<ModelRenderer>();

			// The prefab's contrails and glows replace our plain tracer.
			if ( model is null ) {
				ShowRenderers( !_behindViewModel );
				return;
			}
		}

		if ( model is not null ) {
			var renderer = _visual.AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_visual.WorldScale = 1;
			_model = renderer;
		}
		else {
			_tracer = _visual.AddComponent<LineRenderer>();
			_tracer.UseVectorPoints = true;
			_tracer.VectorPoints = new List<Vector3> { Position, Position };
			_tracer.Color = new Color( 1f, 0.85f, 0.5f, 0.6f );
			_tracer.Width = 0.6f;
			_tracer.Additive = true;
			_tracer.CastShadows = false;
			_tracerTail = Position;
		}

		ShowRenderers( !_behindViewModel );
	}

	// Spawns a projectile prefab with Halo's sound limits, no shadow, and its lights reaching the level.
	// Its contrails are removed because CreateContrails draws them as Halo does.
	static GameObject SpawnPrefab( PrefabFile prefab, Vector3 position, Rotation rotation, GameObject parent, object source ) {
		var go = HaloEffects.SpawnPrefab( prefab, position, rotation.Forward, parent, source );
		if ( !go.IsValid() ) return null;

		go.WorldRotation = rotation;
		go.Transform.ClearInterpolation();
		foreach ( var contrail in go.GetAllObjects( false ).Where( x => x.Tags.Has( "halo_contrail" ) ).ToList() )
			contrail.Destroy();
		if ( go.GetComponent<ModelRenderer>() is { } model )
			model.RenderType = ModelRenderer.ShadowRenderType.Off;

		// Halo's projectile lights only show lens flares; here they also light the level.
		foreach ( var light in go.GetComponentsInChildren<Light>( true ) )
			light.Contribution |= Light.LightContribution.Transmissive;
		return go;
	}

	// The grenade on the "left hand" marker during a throw, its glow, light and hum already going.
	// Pass it on as ProjectileLaunch.HeldVisual, or destroy it.
	// In first person it isn't drawn, since Halo doesn't draw the camera's unit or what's attached to it.
	public static GameObject CreateHeld( string projectile, GameObject hand, Rotation rotation, bool firstPerson ) {
		if ( !HaloEffects.PrefabsEnabled || !hand.IsValid() ) return null;
		var prefab = HaloEffects.Prefab( $"{HaloEffects.WithoutExtension( projectile )}__projectile" );
		if ( prefab is null ) return null;

		var go = SpawnPrefab( prefab, hand.WorldPosition, rotation, hand, null );
		if ( !go.IsValid() ) return null;

		if ( firstPerson ) {
			var hidden = go.AddComponent<HiddenRenderers>();
			foreach ( var renderer in go.GetComponentsInChildren<Renderer>( true ).Where( x => x.Enabled ).ToList() ) {
				renderer.Enabled = false;
				hidden.Renderers.Add( renderer );
			}
		}
		return go;
	}

	// The local player's unit in first person, which Halo doesn't draw along with anything attached to it.
	static bool FirstPersonUnit( IHaloUnit unit ) =>
		unit is SandboxUnit { Player: { } player } && player.IsValid() && player.IsLocalPlayer && unit.IsAlive && !HaloView.ThirdPerson;

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

	// Each contrail starts where it was fired from. Non-tracer shots skip the first one.
	void CreateContrails( Scene scene, PrefabFile prefab ) {
		var system = ContrailSystem.Get( scene );
		if ( system is null ) return;

		var prefabScene = prefab is not null ? SceneUtility.GetPrefabScene( prefab ) : null;
		foreach ( var attachment in ContrailAttachments.For( Launch.Projectile ) ) {
			if ( attachment.Tracer && !Launch.Tracer ) continue;

			var fallback = prefabScene?.GetAllObjects( false ).FirstOrDefault( x => x.Name == attachment.PrefabChild )?.GetComponent<TrailRenderer>( true );
			var definition = ContrailDefinition.Get( attachment.Contrail, fallback );
			var contrail = system.Create( definition, ContrailMarker( attachment, Drawn( Position ) ), ContrailOrientation, Velocity );
			if ( contrail is not null ) _contrails.Add( (contrail, attachment) );
		}
		_contrailAge = Age;
	}

	// The first person barrel, or the held weapon's while zoomed or in third person.
	Vector3? LocalBarrel() {
		if ( LocalPlayerOwner is not { } owner || Launch.Thrown || Launch.Vehicle != Guid.Empty ) return null;
		return HaloView.LocalMuzzle?.Position;
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

	// On a hit, lays points up to the impact so the trail reaches it.
	// Then the contrails are orphaned and stay until their points expire.
	void ReleaseContrails() {
		var outOfRange = Tag.MaximumRange > 0 && HaloUnits.ToWorldUnits( Travelled ) > Tag.MaximumRange;
		foreach ( var (contrail, attachment) in _contrails ) {
			var marker = ContrailMarker( attachment, Drawn( Position ) );
			if ( !outOfRange ) {
				var speed = Velocity.Length;
				var dt = speed > 1 ? MathF.Min( Age - _contrailAge, marker.Distance( contrail.NewestPoint ) / speed ) : 0;
				contrail.OwnerCollision( false, MathF.Max( 0, dt ), marker, ContrailOrientation, Velocity );
			}
			contrail.OwnerCollision( true, 0, marker, ContrailOrientation, Velocity );
		}
		_contrails.Clear();
	}

	// Draws it alpha of the way through the last tick, as Spartans are drawn.
	public void UpdateVisual( float alpha ) {
		// Stuck to a body, it follows the bone as drawn this frame, not as of the last tick.
		if ( AttachBone is not null && AttachedTo.IsValid() ) {
			var body = AttachedCorpse ?? (AttachedTo.IsAlive && !SawDeath ? AttachedTo.Renderer : null);
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
			if ( leftTheEye || !HaloView.LocalViewModelShown || !camera.IsValid() || position.Distance( camera.WorldPosition ) > ViewModelReach ) {
				_behindViewModel = false;
				ShowRenderers( true );
			}
		}

		// Stuck to the first person local player, the model is hidden as Halo hides it; glow, light and sound go on.
		if ( _model.IsValid() && !_behindViewModel )
			_model.Enabled = !(!SawDeath && FirstPersonUnit( AttachedTo ));

		if ( _tracer.IsValid() ) {
			// A short streak behind the round.
			var length = MathF.Min( Travelled, MathF.Min( 300, Velocity.Length * 0.025f ) );
			_tracerTail = position - Velocity.Normal * length;
			_tracer.VectorPoints = new List<Vector3> { _tracerTail, position };
		}
	}

	// Model, lights, glows and sounds go at once. Contrails and trails are left to fade out over their lifetime.
	public void DestroyVisual() {
		ReleaseContrails();
		_carried.Clear();

		if ( !_visual.IsValid() ) {
			_visual = null;
			return;
		}

		var fade = 0f;
		foreach ( var trail in _visual.GetComponentsInChildren<TrailRenderer>() ) {
			trail.Emitting = false;
			fade = MathF.Max( fade, trail.LifeTime );
		}

		// Still hidden behind the view model, so nothing was seen that needs to fade.
		// Its effects and particle systems go with it; the mount's runtime lets their loose particles live out.
		if ( fade <= 0 || _behindViewModel ) {
			_visual.Destroy();
			_visual = null;
			return;
		}

		foreach ( var effect in _visual.GetAllObjects( false ).Where( x => x.Tags.Has( "halo_effect", false ) ).ToList() ) if ( effect.IsValid() ) effect.Destroy();
		foreach ( var renderer in _visual.GetComponentsInChildren<ModelRenderer>() ) renderer.Enabled = false;
		foreach ( var light in _visual.GetComponentsInChildren<Light>() ) light.Enabled = false;
		foreach ( var sound in _visual.GetComponentsInChildren<BaseSoundComponent>() ) sound.StopSound();
		if ( _tracer.IsValid() ) _tracer.Enabled = false;

		_visual.AddComponent<TimedDestroy>().Seconds = fade;
		_visual = null;
	}
}

// A held grenade's renderers hidden in first person, shown again once it's thrown.
public sealed class HiddenRenderers : Component {
	public List<Renderer> Renderers { get; } = new();

	public void Show() {
		foreach ( var renderer in Renderers )
			if ( renderer.IsValid() ) renderer.Enabled = true;
		Renderers.Clear();
		Destroy();
	}
}
