using System.Collections.Concurrent;
using System.Reflection;

// Sandbox's player is a game type a mount can't reference, so a player is the engine's PlayerController and its health is read off whichever damageable component on it has one.
public static class SandboxPlayer
{
	static readonly ConcurrentDictionary<Type, (PropertyInfo Health, PropertyInfo MaxHealth)> HealthProperties = new();

	extension( PlayerController player )
	{
		public PlayerController Controller => player;
		public bool IsLocalPlayer => !player.IsProxy;
		public bool IsSeated => player.GetComponentInParent<Sandbox.Movement.ISitTarget>( false ) is not null;
		public float Health => Read( player, false );
		public float MaxHealth => Read( player, true );

		public static PlayerController FindLocalPlayer() =>
			Game.ActiveScene?.GetAllComponents<PlayerController>().FirstOrDefault( p => !p.IsProxy && (p.Network.Owner is null || p.Network.Owner == Connection.Local) );
	}

	// Without a damageable component that has health (a game without Sandbox's player) the player counts as healthy.
	static float Read( PlayerController player, bool maximum )
	{
		if ( !player.IsValid() ) return 0;
		foreach ( var component in player.GameObject.Components.GetAll<Component.IDamageable>() )
		{
			var properties = HealthProperties.GetOrAdd( component.GetType(), type => (Find( type, "Health" ), Find( type, "MaxHealth" )) );
			if ( properties.Health is null ) continue;
			var value = (maximum ? properties.MaxHealth : properties.Health)?.GetValue( component );
			return value is float f ? f : 100;
		}
		return 100;
	}

	static PropertyInfo Find( Type type, string name ) =>
		type.GetProperty( name, BindingFlags.Instance | BindingFlags.Public ) is { PropertyType: var t } p && t == typeof( float ) ? p : null;
}
