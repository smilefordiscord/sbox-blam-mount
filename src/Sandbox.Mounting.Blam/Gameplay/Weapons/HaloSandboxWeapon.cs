namespace HaloWeapons;

// What our Halo weapons need beyond the engine's weapon, as Sandbox's own weapons have it: a model shown while lying in the world, a throw when dropped and the holder's look slowed while zoomed.
public abstract class HaloSandboxWeapon : BaseCombatWeapon, PlayerController.IEvents
{
	[Property, Feature( "WorldModel" )] public GameObject DroppedGameObject { get; set; }

	[Sync( SyncFlags.FromHost ), Change( nameof( OnIsDroppedChanged ) )]
	public bool IsDropped { get; set; } = true;

	void OnIsDroppedChanged( bool oldValue, bool newValue )
	{
		if ( DroppedGameObject.IsValid() ) DroppedGameObject.Enabled = newValue;
	}

	// Scales the holder's look input this frame.
	public virtual void OnCameraMove( PlayerController player, ref Angles angles ) { }

	void PlayerController.IEvents.OnEyeAngles( ref Angles angles )
	{
		if ( IsActive && !IsProxy && Owner is { } owner ) OnCameraMove( owner, ref angles );
	}

	protected override void OnAdded( BaseInventoryComponent inventory )
	{
		IsDropped = false;
		base.OnAdded( inventory );
	}

	protected override void DestroyWorldModel()
	{
		base.DestroyWorldModel();
		if ( Networking.IsHost ) IsDropped = true;
	}

	// Thrown ahead of the holder's eyes, as Sandbox drops its weapons.
	protected override bool OnDrop()
	{
		var owner = Owner;
		var eye = owner.IsValid() ? owner.EyeTransform : WorldTransform;
		var velocity = owner.IsValid() ? owner.Velocity + eye.Forward * 200 + Vector3.Up * 100 : Vector3.Up * 50;

		Network.DropOwnership();
		GameObject.SetParent( null, true );
		GameObject.Enabled = true;
		WorldPosition = owner.IsValid() ? eye.Position + eye.Forward * 48 : WorldPosition;
		GameObject.Tags.Add( "removable" );
		IsDropped = true;

		if ( GetComponent<Rigidbody>( true ) is { } body )
		{
			body.Velocity = velocity;
			body.AngularVelocity = Vector3.Random * 8;
		}
		return true;
	}
}
