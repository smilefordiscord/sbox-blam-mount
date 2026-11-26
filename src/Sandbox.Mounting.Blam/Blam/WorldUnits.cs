namespace Blam;

// s&box units per Halo world unit, chosen so the player fits Halo's spaces the way the Master Chief does.
// His standing camera height (0.62 world units, from the cyborg biped tag) becomes the s&box player's eye height (64 units).
// His collision height (0.7) then comes out at 72, the s&box player's height, as well.
public static class WorldUnits
{
	public const float SboxScale = 64f / 0.62f;
}
