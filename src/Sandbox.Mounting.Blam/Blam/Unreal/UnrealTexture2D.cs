using System;
using System.Collections.Generic;
using System.IO;

namespace Blam.Unreal;

/// <summary>
/// One mip level of an <see cref="UnrealTexture2D"/>: its pixels inline, or where they are in the
/// package's <c>.ubulk</c> file.
/// </summary>
public sealed record UnrealTextureMip( int Width, int Height, byte[] Inline, long BulkOffset, int BulkSize );

/// <summary>
/// A cooked Texture2D's platform data: its pixel format (<c>PF_DXT1</c>, <c>PF_DXT5</c>,
/// <c>PF_BC5</c>, ...) and mip levels, largest first, plus its <c>SRGB</c> property.
/// </summary>
/// <remarks>
/// After the tagged properties: a no-guid flag, two strip flags, a cooked flag, then per pixel
/// format a name and skip offset followed by the size, packed slice count, format string, first
/// mip and mip count, and per mip a cooked flag, a bulk data record (flags, element count, size on
/// disk, offset) with the pixels inline when it isn't in the separate file, and the mip's size.
/// The list of formats ends with "None"; only the first is read.
/// </remarks>
public sealed class UnrealTexture2D
{
	// Bulk data flags.
	const uint ForceInlinePayload = 0x40;
	const uint PayloadInSeparateFile = 0x100;

	public string PixelFormat { get; }
	public int Width { get; }
	public int Height { get; }
	public bool Srgb { get; }
	public IReadOnlyList<UnrealTextureMip> Mips { get; }

	public UnrealTexture2D( UnrealPackage package )
	{
		var export = package.FindExport( "Texture2D" ) ?? throw new InvalidDataException( "No Texture2D in the package" );
		var properties = package.ReadProperties( export, out var r );

		// SRGB defaults to true and is only saved when false.
		Srgb = !properties.TryGetValue( "SRGB", out var srgb ) || srgb is true;

		r.ReadInt32(); // no object guid
		r.Skip( 4 ); // strip flags (UTexture, UTexture2D)
		if ( r.ReadInt32() == 0 )
			throw new InvalidDataException( "The texture isn't cooked" );

		package.ReadName( r ); // pixel format name
		r.ReadInt64(); // skip offset

		Width = r.ReadInt32();
		Height = r.ReadInt32();
		r.ReadUInt32(); // slices and flags
		PixelFormat = r.ReadFString();
		r.ReadInt32(); // first mip to serialize
		var count = r.ReadInt32();

		var mips = new List<UnrealTextureMip>( count );
		for ( var i = 0; i < count; i++ )
		{
			r.ReadInt32(); // cooked
			var flags = r.ReadUInt32();
			r.ReadInt32(); // element count
			var size = r.ReadInt32();
			var offset = r.ReadInt64();

			byte[] inline = null;
			long bulkOffset = 0;
			if ( (flags & ForceInlinePayload) != 0 || (flags & PayloadInSeparateFile) == 0 )
				inline = r.ReadBytes( size );
			else
				bulkOffset = offset + package.BulkDataStart;

			var width = r.ReadInt32();
			var height = r.ReadInt32();
			r.ReadInt32(); // depth
			mips.Add( new UnrealTextureMip( width, height, inline, bulkOffset, size ) );
		}

		Mips = mips;
	}
}
