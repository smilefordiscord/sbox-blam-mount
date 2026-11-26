using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Light volumes (ltvl effect parts): shown at each place of their location while their event's duration runs, a row of profiles along the place's forward as MCC's light_volume_update lays them out and its vertex shader draws them
// (profile i at offset + i x length / (count - 1), count = length x density; each a view-facing quad thickness wide across the axis and, along it, between its thickness seen end-on and its profile length seen side-on).
sealed partial class Halo3ParticleWorld
{
	sealed class LightVolumeInstance
	{
		public Halo3LightVolumeDefinition Definition;
		public EffectInstance Effect;
		public Place Place;
		public Halo3LightVolumePart Part;
		public float Random, Correlation1, Correlation2;
		public int LastTick;
		public float Age;
		public RenderSystem Render;
	}

	readonly List<LightVolumeInstance> _volumes = new();
	readonly Dictionary<(EffectInstance Effect, int Event, int Part, int Place, int Volume), LightVolumeInstance> _volumesByKey = new();
	readonly float[] _volumeInputs = new float[9];
	int _ticks;

	// Most profiles in one light volume.
	const int MaxProfiles = 256;

	// A step of an event's duration: its light volumes are on at each place of their location.
	void ShowLightVolumes( EffectInstance effect, EventRun run, float age )
	{
		var parts = effect.Definition.Events[run.Event].LightVolumes;
		for ( var pi = 0; pi < parts.Length; pi++ )
		{
			var part = parts[pi];
			var places = effect.Locations[part.Location >= 0 && part.Location < effect.Locations.Length ? part.Location : 0];
			for ( var p = 0; p < places.Count; p++ )
			{
				for ( var v = 0; v < part.Volumes.Length; v++ )
				{
					var key = (effect, run.Event, pi, p, v);
					if ( !_volumesByKey.TryGetValue( key, out var instance ) )
					{
						instance = new LightVolumeInstance { Definition = part.Volumes[v], Effect = effect, Random = Rand(), Correlation1 = Rand(), Correlation2 = Rand() };
						_volumesByKey[key] = instance;
						_volumes.Add( instance );
					}

					instance.Place = places[p];
					instance.Part = part;
					instance.LastTick = _ticks;
					instance.Age = age;
				}
			}
		}
	}

	// The profiles of every light volume shown in the last tick; the ones their effects stopped showing go.
	void SnapshotLightVolumes()
	{
		for ( var i = _volumes.Count - 1; i >= 0; i-- )
		{
			var instance = _volumes[i];
			var shown = instance.LastTick >= _ticks - 1 && instance.Place.Valid && !instance.Effect.Done && !instance.Effect.Dormant;
			if ( !shown )
			{
				if ( instance.Effect.Done || _ticks - instance.LastTick > 30 )
				{
					instance.Render?.Delete();
					_volumes.RemoveAt( i );
					foreach ( var key in _volumesByKey.Where( kv => kv.Value == instance ).Select( kv => kv.Key ).ToList() )
						_volumesByKey.Remove( key );
				}
				else if ( instance.Render?.Object is { } hidden && hidden.IsValid() )
				{
					hidden.RenderingEnabled = false;
				}

				continue;
			}

			SnapshotLightVolume( instance );
		}
	}

	void SnapshotLightVolume( LightVolumeInstance instance )
	{
		var volume = instance.Definition.Volume;
		var render = instance.Render ??= NewRender( instance.Definition.Look, null );
		render.LightVolume = true;
		render.Count = 0;

		var v = _volumeInputs;
		Array.Clear( v );
		v[(int)LightVolumeInput.GameTime] = _gameTime;
		v[(int)LightVolumeInput.Age] = instance.Age;
		v[(int)LightVolumeInput.Random] = instance.Random;
		v[(int)LightVolumeInput.Correlation1] = instance.Correlation1;
		v[(int)LightVolumeInput.Correlation2] = instance.Correlation2;
		v[(int)LightVolumeInput.Lod] = 1;
		v[(int)LightVolumeInput.EffectAScale] = instance.Effect.A;
		v[(int)LightVolumeInput.EffectBScale] = instance.Effect.B;

		var (origin, rotation) = instance.Place.Frame;
		if ( instance.Part is { } placed )
		{
			origin += rotation * placed.Offset;
			rotation *= placed.Orientation;
		}
		var axis = rotation.Forward;
		var length = MathF.Max( volume.Length.Evaluate( v, 0 ), 0 );
		var offset = volume.Offset.Evaluate( v, 0 );
		var count = Math.Clamp( (int)MathF.Round( length * MathF.Max( volume.ProfileDensity.Evaluate( v, 1 ), 0 ) ), 1, MaxProfiles );
		var spacing = count > 1 ? length / (count - 1) : 0;

		var mins = new Vector3( float.MaxValue );
		var maxs = new Vector3( float.MinValue );
		var reach = 0f;
		for ( var i = 0; i < count; i++ )
		{
			v[(int)LightVolumeInput.ProfilePosition] = count > 1 ? i / (float)(count - 1) : 0;
			var thickness = MathF.Max( volume.ProfileThickness.Evaluate( v, 0.1f ), 0 );
			var profileLength = MathF.Max( volume.ProfileLength.Evaluate( v, 0.1f ), 0 );
			var color = volume.ProfileColor.EvaluateColor( v );
			var intensity = MathF.Max( volume.ProfileIntensity.Evaluate( v, 1 ), 0 );
			var alpha = Math.Clamp( volume.ProfileAlpha.Evaluate( v, 1 ), 0, 1 ) * intensity;
			if ( alpha <= 0 || thickness <= 0 )
				continue;

			var position = (origin + axis * (offset + i * spacing)) * Units;
			if ( render.Count == render.Items.Length )
				Array.Resize( ref render.Items, render.Count * 2 );

			render.Items[render.Count++] = new DrawItem
			{
				Position = position,
				Direction = axis,
				Size = thickness * Units,
				Aspect = profileLength * Units,
				Color = new Vector4( color.X * intensity, color.Y * intensity, color.Z * intensity, Math.Min( alpha, 1 ) ),
				Sprite = new Vector4( 0, 0, 1, 1 ),
				Next = new Vector4( 0, 0, 1, 1 ),
			};

			mins = mins.ComponentMin( position );
			maxs = maxs.ComponentMax( position );
			reach = MathF.Max( reach, MathF.Max( thickness, profileLength ) * Units );
		}

		lock ( render.Lock )
		{
			(render.Items, render.Drawing) = (render.Drawing, render.Items);
			render.DrawingCount = render.Count;
		}

		if ( render.Object is { } so && so.IsValid() )
		{
			so.RenderingEnabled = render.DrawingCount > 0;
			if ( render.DrawingCount > 0 )
				so.Bounds = new BBox( mins - reach, maxs + reach );
		}
	}

	// A light volume profile's quad: u along the axis as seen (between its thickness end-on and its profile length side-on), v across it.
	static bool AddProfile( Vertex[] vertices, int at, in DrawItem item, Vector4 color, Vector3 toItem )
	{
		var view = toItem.Normal;
		var across = item.Direction.Cross( view );
		var side = across.Length;
		if ( side < 1e-5f )
		{
			// Seen end-on: any axis across the view.
			across = view.Cross( MathF.Abs( view.z ) < 0.99f ? Vector3.Up : Vector3.Forward );
			side = 0;
		}

		across = across.Normal;
		var along = across.Cross( view ).Normal;
		var length = item.Size + (item.Aspect - item.Size) * Math.Clamp( side, 0, 1 );

		for ( var c = 0; c < 4; c++ )
		{
			float u = c is 1 or 2 ? 1 : 0;
			float v = c >= 2 ? 1 : 0;
			ref var corner = ref vertices[at + c];
			corner.Position = item.Position + along * ((u - 0.5f) * length) + across * ((v - 0.5f) * item.Size);
			corner.Color = Color32.White;
			corner.TexCoord0 = new Vector4( u, v, u, v );
			corner.TexCoord1 = color;
			corner.Normal = new Vector3( u, v, 0 );
			corner.Tangent = Vector4.Zero;
		}

		return true;
	}
}
