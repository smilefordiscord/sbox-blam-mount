using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Every model tag (hlmt) to register as mount://halo3/<hlmt path>.vmdl (Halo3Mount.ModelPath), built by RenderModelLoader.
// Campaign maps keep some tags whose geometry was stripped, so each model keeps every map's copy: live geometry first, then the most variants, then map order.
sealed class Halo3Models
{
	readonly Dictionary<string, List<(string MapPath, uint TagId, bool Live, int Variants, int Order)>> _models = new( StringComparer.OrdinalIgnoreCase );
	int _order;

	// Reads only tag data (no resource pages), so it's quick at mount time.
	public void Find( CacheFile cache )
	{
		foreach ( var tag in cache.TagsOf( "hlmt" ) )
		{
			if ( string.IsNullOrEmpty( tag.Path ) || tag.Address == 0 )
				continue;

			var hlmt = new ModelTag( tag );
			if ( hlmt.RenderModel is not { } renderModel )
				continue;

			var live = cache.Resources.Get( renderModel.Data.U32( RenderGeometry.RenderModelOffset + 0x7C ) ) is not null;
			var path = Halo3Mount.ResourcePath( tag.Path );
			if ( !_models.TryGetValue( path, out var sources ) )
				_models[path] = sources = new();
			sources.Add( (cache.FilePath, tag.Id, live, hlmt.VariantCount, _order++) );
		}
	}

	public IEnumerable<string> Paths => _models.Keys;

	public void Register( MountContext context )
	{
		foreach ( var (path, sources) in _models )
		{
			var ordered = sources.OrderByDescending( s => s.Live ).ThenByDescending( s => s.Variants ).ThenBy( s => s.Order ).Select( s => (s.MapPath, s.TagId) ).ToList();
			context.Add( ResourceType.Model, path, new RenderModelLoader( ordered ) );
		}
	}
}
