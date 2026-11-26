using System;
using System.Collections.Generic;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Halo's visibility for one structure BSP: which clusters a camera's cluster can see, which
/// structure surfaces are drawn, and which clusters an object's bounding sphere reaches into.
/// </summary>
/// <remarks>
/// A camera draws its cluster and every cluster that cluster's potentially visible set holds, all
/// their surfaces and every object reaching into one, whichever way it faces: what's outside the
/// view is left to s&amp;box's frustum culling. With the camera outside the level everything is drawn.
/// </remarks>
public sealed class PortalVisibility
{
	readonly BspVisibility _data;

	// Per cluster: drawn this view.
	readonly bool[] _rendered;
	readonly List<int> _renderedClusters = new();
	bool _computed;

	// The clusters-in-sphere flood's marks.
	readonly int[] _sphereMarks;
	int _sphereStamp;

	public PortalVisibility( BspVisibility data )
	{
		_data = data;
		var count = data.Clusters.Count;
		_rendered = new bool[count];
		_sphereMarks = new int[count];

		var subclusters = 0;
		SubclusterStart = new int[count];
		for ( var i = 0; i < count; i++ )
		{
			SubclusterStart[i] = subclusters;
			subclusters += data.Clusters[i].Subclusters.Length;
		}
		SubclusterCount = subclusters;
	}

	public BspVisibility Data => _data;

	/// <summary>The cluster the last view was from, or -1 for none (everything drawn).</summary>
	public int CameraCluster { get; private set; } = -1;

	/// <summary>The clusters drawn by the last view.</summary>
	public IReadOnlyList<int> RenderedClusters => _renderedClusters;

	/// <summary>Each cluster's first subcluster in a numbering of all the BSP's subclusters.</summary>
	public int[] SubclusterStart { get; }
	public int SubclusterCount { get; }

	public bool IsRendered( int cluster ) => CameraCluster < 0 || (cluster >= 0 && cluster < _rendered.Length && _rendered[cluster]);

	/// <summary>
	/// The cluster a point is in, or -1 if it's outside the level or in solid geometry.
	/// </summary>
	public int FindCluster( Vector3 point )
	{
		var cluster = _data.Tree.FindCluster( point );
		return cluster >= 0 && cluster < _data.Clusters.Count ? cluster : -1;
	}

	/// <summary>
	/// Works out what a camera in a cluster sees (-1: outside the level, so everything).
	/// </summary>
	public void Compute( int cluster )
	{
		cluster = cluster >= 0 && cluster < _data.Clusters.Count ? cluster : -1;
		if ( _computed && cluster == CameraCluster )
			return;

		_computed = true;
		CameraCluster = cluster;
		foreach ( var drawn in _renderedClusters )
			_rendered[drawn] = false;
		_renderedClusters.Clear();

		for ( var i = 0; i < _data.Clusters.Count; i++ )
		{
			if ( cluster < 0 || i == cluster || _data.Sees( cluster, i ) )
			{
				_rendered[i] = true;
				_renderedClusters.Add( i );
			}
		}
	}

	/// <summary>
	/// Marks the subclusters drawn by the last view (numbered as <see cref="SubclusterStart"/>).
	/// </summary>
	public void MarkSubclusters( bool[] marks )
	{
		Array.Clear( marks );
		foreach ( var cluster in _renderedClusters )
			Array.Fill( marks, true, SubclusterStart[cluster], _data.Clusters[cluster].Subclusters.Length );
	}

	/// <summary>
	/// Whether an object reaching into these clusters is drawn by the last view. An object in no
	/// cluster is drawn only while the camera is outside the level.
	/// </summary>
	public bool ObjectVisible( List<int> clusters )
	{
		if ( CameraCluster < 0 )
			return true;

		foreach ( var cluster in clusters )
		{
			if ( _rendered[cluster] )
				return true;
		}

		return false;
	}

	/// <summary>
	/// The clusters a sphere reaches into from the cluster its center is in: through each portal
	/// the sphere touches, on to the cluster behind it.
	/// </summary>
	public void ClustersInSphere( int cluster, Vector3 center, float radius, List<int> result )
	{
		result.Clear();
		if ( cluster < 0 || cluster >= _data.Clusters.Count )
			return;

		if ( radius <= 0 )
		{
			result.Add( cluster );
			return;
		}

		if ( ++_sphereStamp == int.MaxValue )
		{
			Array.Clear( _sphereMarks );
			_sphereStamp = 1;
		}
		SphereFlood( cluster, center, radius, result );
	}

	void SphereFlood( int cluster, Vector3 center, float radius, List<int> result )
	{
		result.Add( cluster );
		_sphereMarks[cluster] = _sphereStamp;

		foreach ( var portalIndex in _data.Clusters[cluster].Portals )
		{
			if ( portalIndex < 0 || portalIndex >= _data.Portals.Count )
				continue;

			var portal = _data.Portals[portalIndex];
			var next = portal.FrontCluster == cluster ? portal.BackCluster : portal.FrontCluster;
			if ( next < 0 || next >= _data.Clusters.Count || _sphereMarks[next] == _sphereStamp || !SphereTouchesPortal( portal, center, radius ) )
				continue;

			SphereFlood( next, center, radius, result );
		}
	}

	/// <summary>
	/// Whether a sphere touches a portal: it reaches the portal's plane, its bounding sphere, and
	/// the circle it cuts out of the plane overlaps the portal's outline.
	/// </summary>
	static bool SphereTouchesPortal( BspPortal portal, Vector3 center, float radius )
	{
		if ( portal.Plane is not { } plane || portal.Vertices.Length < 3 )
			return false;

		var normal = new Vector3( plane.X, plane.Y, plane.Z );
		var distance = Vector3.Dot( normal, center ) - plane.W;
		var reach = portal.Radius + radius;
		if ( MathF.Abs( distance ) >= radius || Vector3.DistanceSquared( center, portal.Centroid ) >= reach * reach )
			return false;

		// Onto the axis plane the portal faces most, kept the same way round.
		var axis = MathF.Abs( normal.Z ) >= MathF.Abs( normal.Y ) && MathF.Abs( normal.Z ) >= MathF.Abs( normal.X ) ? 2 : MathF.Abs( normal.Y ) >= MathF.Abs( normal.X ) ? 1 : 0;
		var positive = (axis == 0 ? normal.X : axis == 1 ? normal.Y : normal.Z) > 0;
		var circle = Project( center - normal * distance, axis, positive );
		var circleRadius = MathF.Sqrt( radius * radius - distance * distance );

		var vertices = portal.Vertices;
		for ( var i = 0; i < vertices.Length; i++ )
		{
			var a = Project( vertices[i], axis, positive );
			var b = Project( vertices[i + 1 < vertices.Length ? i + 1 : 0], axis, positive );
			var edge = b - a;
			var lengthSquared = edge.LengthSquared();
			if ( lengthSquared == 0 )
				continue;

			var offset = circle - a;
			var cross = offset.X * edge.Y - offset.Y * edge.X;
			if ( cross > 0 && cross * cross > lengthSquared * circleRadius * circleRadius )
				return false;
		}

		return true;
	}

	static Vector2 Project( Vector3 p, int axis, bool positive ) => axis switch
	{
		0 => positive ? new Vector2( p.Y, p.Z ) : new Vector2( p.Z, p.Y ),
		1 => positive ? new Vector2( p.Z, p.X ) : new Vector2( p.X, p.Z ),
		_ => positive ? new Vector2( p.X, p.Y ) : new Vector2( p.Y, p.X ),
	};
}
