using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A mesh's index buffer rewritten with the pieces drawn: the caller fills <see cref="Building"/>
/// from the whole set and hands over how much of it to draw. Nothing is uploaded unless it changed.
/// </summary>
sealed class VisibleIndices( Mesh mesh, int[] indices )
{
	readonly int[] _drawn = (int[])indices.Clone();
	int _count = indices.Length;

	/// <summary>Every index, as the mesh was built.</summary>
	public int[] All { get; } = indices;

	/// <summary>Where the indices to draw are put together.</summary>
	public int[] Building { get; } = new int[indices.Length];

	/// <summary>Index count drawn now; 0 hides the renderer.</summary>
	public int Count => _count;

	/// <summary>Draws the first <paramref name="count"/> indices of <see cref="Building"/>.</summary>
	public void Draw( int count ) => Draw( Building, count );

	public void DrawAll() => Draw( All, All.Length );

	void Draw( int[] source, int count )
	{
		if ( count == _count && source.AsSpan( 0, count ).SequenceEqual( _drawn.AsSpan( 0, count ) ) )
			return;

		_count = count;
		if ( count == 0 )
			return;

		Array.Copy( source, _drawn, count );
		mesh.SetIndexBufferData( _drawn.AsSpan( 0, count ), 0 );
		mesh.SetIndexRange( 0, count );
	}
}

/// <summary>
/// One mesh of a structure BSP's detail objects, drawn as Halo draws them: those of the 8 world
/// unit cells around the camera's, the camera's own included (three by three by three).
/// </summary>
/// <param name="cells">Each cell's coordinates and its run of the mesh's indices, which hold the cells one after another.</param>
sealed class DetailObjectCells( Mesh mesh, int[] indices, (int X, int Y, int Z, int First, int Count)[] cells )
{
	readonly VisibleIndices _indices = new( mesh, indices );
	(int X, int Y, int Z)? _around;

	/// <summary>Index count drawn now; 0 hides the renderer.</summary>
	public int Count => _indices.Count;

	/// <summary>The cell a point (world units) is in, as Halo rounds it.</summary>
	public static (int X, int Y, int Z) CellOf( System.Numerics.Vector3 point ) => (Cell( point.X ), Cell( point.Y ), Cell( point.Z ));

	static int Cell( float coordinate ) => (int)Math.Round( (double)(coordinate * (1 / DetailObjects.CellSize) - 0.5f), MidpointRounding.ToEven );

	/// <summary>Draws the objects of the cells around this one.</summary>
	public void ShowAround( (int X, int Y, int Z) camera )
	{
		if ( _around == camera )
			return;
		_around = camera;

		var count = 0;
		foreach ( var (x, y, z, first, length) in cells )
		{
			if ( Math.Abs( x - camera.X ) > 1 || Math.Abs( y - camera.Y ) > 1 || Math.Abs( z - camera.Z ) > 1 )
				continue;

			Array.Copy( _indices.All, first, _indices.Building, count, length );
			count += length;
		}

		_indices.Draw( count );
	}

	public void DrawAll()
	{
		_around = null;
		_indices.DrawAll();
	}
}

/// <summary>
/// One mesh of a level's decals, drawn as Halo draws them: those in the clusters the view draws.
/// </summary>
/// <param name="clusters">For each BSP, the cluster each of the mesh's quads is in (-1 for none).</param>
sealed class DecalClusters( Mesh mesh, int[] indices, IReadOnlyList<(BspDrawing Bsp, int[] Clusters)> clusters )
{
	const int IndicesPerQuad = 6;

	readonly VisibleIndices _indices = new( mesh, indices );
	BspDrawing _shownFor;
	int _shownVersion = -1;

	/// <summary>Index count drawn now; 0 hides the renderer.</summary>
	public int Count => _indices.Count;

	/// <summary>Draws the quads in the clusters a BSP's view draws (see <see cref="BspDrawing.ClustersDrawn"/>).</summary>
	public void Show( BspDrawing bsp )
	{
		if ( bsp == _shownFor && bsp.ClustersVersion == _shownVersion )
			return;
		_shownFor = bsp;
		_shownVersion = bsp.ClustersVersion;

		int[] quadClusters = null;
		for ( var i = 0; i < clusters.Count; i++ )
		{
			if ( clusters[i].Bsp == bsp )
				quadClusters = clusters[i].Clusters;
		}

		var count = 0;
		if ( quadClusters is not null )
		{
			var drawn = bsp.ClustersDrawn;
			for ( var quad = 0; quad < quadClusters.Length; quad++ )
			{
				var cluster = quadClusters[quad];
				if ( cluster < 0 || !drawn[cluster] )
					continue;

				Array.Copy( _indices.All, quad * IndicesPerQuad, _indices.Building, count, IndicesPerQuad );
				count += IndicesPerQuad;
			}
		}

		_indices.Draw( count );
	}

	public void DrawAll()
	{
		_shownFor = null;
		_indices.DrawAll();
	}
}
