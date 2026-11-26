using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's damage to an object that isn't a unit, such as a crate: its body vitality and its sections' responses.
// Host only. A section fills up between the last fired response's threshold and the next one's, then that band fires.
// Dying gives full damage to the sections that take it then. What a response does to the world is the owner's.
public sealed class ObjectDamage {
	// What a response shows: an effect at one marker and a damage effect at another, each optional.
	public readonly record struct Effects( string Effect, string EffectMarker, string Damage, string DamageMarker );

	readonly ModelTag _model;
	readonly DamageInfoTag _info;
	readonly SectionState[] _sections;
	readonly Action<Effects> _play;
	readonly Action _destroy;
	Vitality _vitality = Vitality.Full( 0 );

	struct SectionState {
		// 0 to 255 across the current band.
		public int Damage;
		public int Fired;
		// A delayed response waiting to fire, -1 for none.
		public int Pending;
		public int PendingTicks;
	}

	public bool Dead { get; private set; }

	// The last to damage it, whom its explosions credit.
	public Guid LastAttacker { get; private set; }
	public string LastWeapon { get; private set; }

	ObjectDamage( ModelTag model, Action<Effects> play, Action destroy ) {
		_model = model;
		_info = model.DamageInfo;
		_sections = _info.DamageSections.Select( _ => new SectionState { Pending = -1 } ).ToArray();
		_play = play;
		_destroy = destroy;
	}

	// Null for a model without vitality, which damage never touches.
	// play shows a response's effects and deals its damage; destroy removes the object.
	public static ObjectDamage For( ModelTag model, Action<Effects> play, Action destroy ) =>
		model?.DamageInfo is { MaximumVitality: > 0 } ? new ObjectDamage( model, play, destroy ) : null;

	// A direct hit strikes the model material with the surface's global material; area damage the indirect section.
	public void Apply( GlobalsTag globals, DamageEffectTag tag, DamageEvent e, string globalMaterial ) {
		if ( tag is null ) return;

		var hit = e.AreaOfEffect ? null : _model.Materials?.FirstOrDefault( x => x.GlobalMaterialName == globalMaterial ) ?? _model.Materials?.FirstOrDefault();
		var random = new HaloRandom( e.Seed );
		var damage = HaloDamage.Amount( tag, e.Scale, ref random ) * HaloDamage.TableMultiplier( globals, tag, hit?.GlobalMaterialName ?? _info.GlobalIndirectMaterialName );
		if ( damage <= 0 ) return;

		if ( e.AttackerId != Guid.Empty ) {
			LastAttacker = e.AttackerId;
			LastWeapon = e.WeaponName;
		}

		var fraction = damage / _info.MaximumVitality;
		var index = hit is not null ? hit.DamageSection : _info.IndirectDamageSection;
		var section = _info.Section( index );
		if ( section is { VitalityPercentage: > 0 } ) DamageSection( index.Value, fraction / section.VitalityPercentage );
		if ( Dead ) return;

		if ( !(section?.HasFlag( "absorbs body damage" ) ?? false) ) {
			_vitality.Body -= fraction;
			if ( _info.HasFlag( "cannot die from damage" ) ) _vitality.Body = MathF.Max( _vitality.Body, BodyEpsilon );
			if ( damage > _info.MinimumStunDamage ) _vitality.BodyStunTicks = Ticks( _info.StunTime );
		}
		if ( _vitality.Body <= 0 ) Kill();
	}

	// Halo's real epsilon, the least a body that can't die keeps.
	const float BodyEpsilon = 0.0001f;

	// One tick: the body recharges while alive and delayed responses count down.
	public void Tick() {
		if ( !Dead ) HaloDamage.Update( _info, ref _vitality, 0 );
		for ( var i = 0; i < _sections.Length; i++ ) {
			ref var s = ref _sections[i];
			if ( s.Pending < 0 || --s.PendingTicks > 0 ) continue;
			var pending = s.Pending;
			s.Pending = -1;
			Execute( i, pending );
		}
	}

	// damage is a fraction of the section's vitality.
	void DamageSection( int index, float damage ) {
		var responses = _info.DamageSections[index].InstantResponses;
		ref var s = ref _sections[index];

		// The band runs from the last fired response's threshold down to the first unfired one's.
		float upper = 1, lower = 0;
		for ( var r = 0; r < responses.Count; r++ ) {
			if ( Fired( s, r ) ) {
				upper = responses[r].DamageThreshold;
				continue;
			}
			lower = responses[r].DamageThreshold;
			break;
		}

		var band = damage / MathF.Max( upper - lower, 1 / 255f );
		if ( band > 0 ) s.Damage = Math.Clamp( (int)(band * 255 + s.Damage), 0, 255 );
		if ( s.Damage < 255 ) return;

		s.Damage = 0;
		float? threshold = null;
		for ( var r = 0; r < responses.Count; r++ ) {
			if ( Fired( _sections[index], r ) ) continue;
			threshold ??= responses[r].DamageThreshold;
			if ( responses[r].DamageThreshold != threshold ) break;
			Trigger( index, r );
		}
	}

	// Dead objects stay; their "takes full damage when object dies" sections fire every response left at threshold 0.
	void Kill() {
		if ( Dead ) return;
		Dead = true;
		_vitality.Body = 0;
		for ( var i = 0; i < _sections.Length; i++ ) {
			var section = _info.DamageSections[i];
			if ( !section.HasFlag( "takes full damage when object dies" ) ) continue;
			for ( var r = 0; r < section.InstantResponses.Count; r++ ) {
				if ( !Fired( _sections[i], r ) && section.InstantResponses[r].DamageThreshold == 0 ) Trigger( i, r );
			}
		}
	}

	// A skipped response counts as fired. A delayed one plays its delay effect and waits, firing any other still waiting.
	void Trigger( int index, int r ) {
		var response = _info.DamageSections[index].InstantResponses[r];
		ref var s = ref _sections[index];
		if ( response.SkipFraction > 0 && response.SkipFraction > Game.Random.Float() ) {
			s.Fired |= 1 << r;
			return;
		}

		if ( response.ResponseDelay <= 0 ) {
			Execute( index, r );
			return;
		}

		if ( s.Pending >= 0 && s.Pending != r ) {
			var earlier = s.Pending;
			s.Pending = -1;
			Execute( index, earlier );
		}
		s.Pending = r;
		s.PendingTicks = Ticks( response.ResponseDelay );
		if ( response.DelayEffect is not null ) _play( new Effects( response.DelayEffect, response.DelayEffectMarkerName, null, null ) );
	}

	void Execute( int index, int r ) {
		var response = _info.DamageSections[index].InstantResponses[r];
		_sections[index].Fired |= 1 << r;
		_play( new Effects( response.TransitionEffect, response.EffectMarkerName, response.TransitionDamageEffect, response.DamageEffectMarkerName ) );
		if ( response.HasFlag( "kills object" ) ) Kill();
		if ( response.HasFlag( "destroys object" ) ) _destroy();
	}

	static bool Fired( in SectionState s, int r ) => (s.Fired & (1 << r)) != 0;

	static int Ticks( float seconds ) => (int)(seconds * HaloUnits.TicksPerSecond + 0.5f);
}
