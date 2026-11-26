using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Processes a folder of game code with s&box's code generator (Sandbox.Generator, from the installed s&box), as s&box does for game code it
// compiles and its CodeGen tool does for the engine's projects: [Sync], [Rpc] and the other code generator attributes are woven in.
// Writes every source, processed, to <folder>/obj/.generated/<its relative path>, leaving unchanged ones alone, and deletes stale ones.
// Arguments: the folder, then the assemblies the code references (or @file listing them, one per line) and any .cs files it needs to
// compile that aren't written out (the project's global usings).
var folder = Path.GetFullPath( args[0] );
var output = Path.Combine( folder, "obj", ".generated" );
var inputs = args.Skip( 1 ).SelectMany( a => a.StartsWith( '@' ) ? File.ReadAllLines( a[1..] ) : [a] ).Select( a => a.Trim().Trim( '"' ) ).Where( File.Exists ).ToArray();
var references = inputs.Where( a => !a.EndsWith( ".cs", StringComparison.OrdinalIgnoreCase ) );
var context = inputs.Where( a => a.EndsWith( ".cs", StringComparison.OrdinalIgnoreCase ) ).Select( Path.GetFullPath );

var parse = CSharpParseOptions.Default.WithLanguageVersion( LanguageVersion.Preview );
var files = Directory.EnumerateFiles( folder, "*.cs", SearchOption.AllDirectories )
	.Where( f => !Path.GetRelativePath( folder, f ).Replace( Path.DirectorySeparatorChar, '/' ).StartsWith( "obj/" ) )
	.Order( StringComparer.OrdinalIgnoreCase )
	.ToArray();
var trees = files.Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, path: f, encoding: System.Text.Encoding.UTF8 ) ).ToList();

var contextTrees = context.Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, path: f, encoding: System.Text.Encoding.UTF8 ) ).ToList();
var compilation = CSharpCompilation.Create( "GameplayCodeGen", [.. trees, .. contextTrees],
	[.. references.Select( r => MetadataReference.CreateFromFile( r ) )],
	new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary ).WithAllowUnsafe( true ) );

var processor = new Sandbox.Generator.Processor
{
	ILHotloadSupported = false,
	IgnoreDiagnostics = true,
	AddonName = "blam",
	PackageAssetResolver = p => $"/{p}/model_mock.mdl",
	AddonFileMap = files.ToDictionary( f => f, f => Path.GetRelativePath( folder, f ), StringComparer.OrdinalIgnoreCase ),
};
processor.Run( compilation, trees );
if ( processor.Exception is not null )
{
	Console.Error.WriteLine( $"GameplayCodeGen: {processor.Exception}" );
	return 1;
}

var written = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
foreach ( var tree in processor.Compilation.SyntaxTrees )
{
	if ( contextTrees.Any( c => c.FilePath == tree.FilePath ) ) continue;
	var source = Path.IsPathRooted( tree.FilePath ) ? tree.FilePath : Path.Combine( folder, tree.FilePath );
	var target = Path.Combine( output, Path.GetRelativePath( folder, source ) );
	written.Add( target );
	var text = tree.ToString();
	if ( File.Exists( target ) && File.ReadAllText( target ) == text ) continue;
	Directory.CreateDirectory( Path.GetDirectoryName( target ) );
	File.WriteAllText( target, text );
}

if ( Directory.Exists( output ) )
	foreach ( var stale in Directory.EnumerateFiles( output, "*.cs", SearchOption.AllDirectories ).Where( f => !written.Contains( f ) ) )
		File.Delete( stale );

Console.WriteLine( $"GameplayCodeGen: {files.Length} files" );
return 0;
