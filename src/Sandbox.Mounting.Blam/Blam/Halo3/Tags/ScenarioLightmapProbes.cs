using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// One light probe: 36 half floats, dominant light direction xyz, 0, its intensity rgb, 0, then L2 SH coefficients c0..c8 per channel (R, G, B), 0 (lighting.md §4.1).
public sealed record LightProbe( Vector3 DominantDirection, Vector3 DominantIntensity, Vector3[] Coefficients )
{
	public const int Size = 72;

	public bool IsZero => DominantDirection == Vector3.Zero && DominantIntensity == Vector3.Zero && Coefficients.All( c => c == Vector3.Zero );

	public static LightProbe Read( TagView view, int field )
	{
		var h = new float[36];
		for ( var i = 0; i < h.Length; i++ )
			h[i] = (float)BitConverter.UInt16BitsToHalf( view.U16( field + 2 * i ) );

		var coefficients = new Vector3[9];
		for ( var c = 0; c < 9; c++ )
			coefficients[c] = new Vector3( h[8 + c], h[17 + c], h[26 + c] );

		return new LightProbe( new Vector3( h[0], h[1], h[2] ), new Vector3( h[4], h[5], h[6] ), coefficients );
	}

	// A probe from L2 coefficients alone, its dominant light derived as Halo 3 does: the direction of the luminance L1, the intensity per channel the least squares fit of the SH basis at that direction to the coefficients.
	public static LightProbe FromCoefficients( Vector3[] c )
	{
		var luma = new Vector3( 0.212656f, 0.715158f, 0.072186f );
		var d = new Vector3( -Vector3.Dot( c[3], luma ), -Vector3.Dot( c[1], luma ), Vector3.Dot( c[2], luma ) );
		d = d.LengthSquared() > 1e-12f ? Vector3.Normalize( d ) : Vector3.UnitZ;
		var y = new[]
		{
			0.282095f, -0.488603f * d.Y, 0.488603f * d.Z, -0.488603f * d.X,
			1.092548f * d.X * d.Y, -1.092548f * d.Y * d.Z, 0.315392f * (3 * d.Z * d.Z - 1), -1.092548f * d.X * d.Z, 0.546274f * (d.X * d.X - d.Y * d.Y),
		};
		var fit = Vector3.Zero;
		var norm = 0f;
		for ( var k = 0; k < 9; k++ )
		{
			fit += y[k] * c[k];
			norm += y[k] * y[k];
		}
		var intensity = Vector3.Max( fit, Vector3.Zero ) / Math.Max( norm, 1e-4f );
		return new LightProbe( d, intensity, c );
	}
}

// A placed scenery object's probe, by its placement's 8-byte object id (u32 unique id, i16 origin BSP, u8 type, u8 source).
public sealed record SceneryProbe( ulong ObjectId, LightProbe Probe )
{
	public uint UniqueId => (uint)ObjectId;
}

// A machine's (device's) probes: bounds and probes at points in them.
public sealed record MachineProbes( ulong ObjectId, Vector3 Minimum, Vector3 Maximum, IReadOnlyList<(Vector3 Position, LightProbe Probe)> Probes )
{
	public uint UniqueId => (uint)ObjectId;
}

public sealed record AirProbe( Vector3 Position, string Name, ushort BspMask, LightProbe Probe );

// The probes of a scenario's lightmap (sLdT, scnr +0x6AC) that light objects: per placed scenery, per machine, and air probes (lighting.md §1.1, §4.3).
public sealed class ScenarioLightmapProbes
{
	public static readonly TagClass Class = new( "sLdT" );

	public TagEntry Tag { get; }
	public IReadOnlyList<AirProbe> AirProbes { get; }
	public IReadOnlyList<SceneryProbe> Scenery { get; }
	public IReadOnlyList<MachineProbes> Machines { get; }

	readonly Dictionary<ulong, LightProbe> _scenery = new();
	readonly Dictionary<ulong, MachineProbes> _machines = new();

	// The scenario's, or null if it has none.
	public static ScenarioLightmapProbes For( ScenarioTag scenario ) =>
		scenario.Lightmaps is { Address: not 0 } tag && tag.Class == Class ? new ScenarioLightmapProbes( tag ) : null;

	public ScenarioLightmapProbes( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		AirProbes = data.Block( 0x1C, 0x5C ).Select( e => new AirProbe( e.Vec3( 0 ), e.StringId( 0xC ), e.U16( 0x10 ), LightProbe.Read( e, 0x14 ) ) ).ToList();
		Scenery = data.Block( 0x28, 0x50 ).Select( e => new SceneryProbe( ObjectId( e ), LightProbe.Read( e, 8 ) ) ).ToList();
		Machines = data.Block( 0x34, 0x2C ).Select( e => new MachineProbes( ObjectId( e ),
			new Vector3( e.F32( 0x08 ), e.F32( 0x10 ), e.F32( 0x18 ) ), new Vector3( e.F32( 0x0C ), e.F32( 0x14 ), e.F32( 0x1C ) ),
			e.Block( 0x20, 0x54 ).Select( p => (p.Vec3( 0 ), LightProbe.Read( p, 0xC ))).ToList() ) ).ToList();

		foreach ( var probe in Scenery )
			_scenery.TryAdd( probe.ObjectId, probe.Probe );
		foreach ( var machine in Machines )
			_machines.TryAdd( machine.ObjectId, machine );
	}

	static ulong ObjectId( TagView e ) => e.U32( 0 ) | (ulong)e.U32( 4 ) << 32;

	// The probe baked for a scenery placement, or null (dynamic objects sample the lightmap instead).
	public LightProbe ForScenery( ScenarioPlacement placement ) => _scenery.GetValueOrDefault( placement.ObjectId );

	public MachineProbes ForMachine( ScenarioPlacement placement ) => _machines.GetValueOrDefault( placement.ObjectId );
}
