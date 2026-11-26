using System;
using System.Collections.Generic;
using System.IO;

namespace Blam.Unreal;

/// <summary>
/// One object a package defines.
/// </summary>
/// <param name="Class">The object's class name (<c>Texture2D</c>, <c>MaterialInstanceConstant</c>).</param>
/// <param name="SerialOffset">Where the object's data starts in <see cref="UnrealPackage.Data"/>.</param>
public sealed record UnrealExport( string Class, string Name, long SerialOffset, long SerialSize );

/// <summary>
/// A cooked Unreal Engine 4 package: the <c>.uasset</c> header and the <c>.uexp</c> holding its
/// objects' data, as MCC's menus ship them (file version 517, cooked with editor-only data filtered
/// out, properties tagged). Reads the name, import and export tables and objects' tagged properties.
/// </summary>
/// <remarks>
/// Object references are package indices: negative for an import (−1 is the first), positive for
/// an export (1 is the first). <see cref="ObjectName"/> resolves them to the object's name; for
/// imports of other packages' assets that's the asset's name, e.g. a texture's.
/// </remarks>
public sealed class UnrealPackage
{
	const uint Magic = 0x9E2A83C1;
	const uint FilterEditorOnly = 0x80000000;

	readonly string[] _names;
	readonly (string Class, string Name, int Outer)[] _imports;

	/// <summary>The header and the export data together: export offsets point into this.</summary>
	public byte[] Data { get; }
	public IReadOnlyList<UnrealExport> Exports { get; }

	/// <summary>
	/// Offsets of bulk data stored in the separate <c>.ubulk</c> file are relative to this.
	/// </summary>
	public long BulkDataStart { get; }

	public UnrealPackage( byte[] header, byte[] exports )
	{
		var r = new UnrealReader( header );
		if ( r.ReadUInt32() != Magic )
			throw new InvalidDataException( "Not an Unreal package (no magic)" );

		var legacyVersion = r.ReadInt32();
		if ( legacyVersion != -7 )
			throw new NotSupportedException( $"Unreal package file version {legacyVersion} isn't supported" );

		r.ReadInt32(); // UE3 version
		var version = r.ReadInt32();
		r.ReadInt32(); // licensee version
		r.Skip( r.ReadInt32() * 20 ); // custom versions: guid and version each

		var totalHeaderSize = r.ReadInt32();
		r.ReadFString(); // folder name
		var packageFlags = r.ReadUInt32();
		if ( version < 516 || (packageFlags & FilterEditorOnly) == 0 )
			throw new NotSupportedException( "Only cooked UE4 packages are supported" );

		var nameCount = r.ReadInt32();
		var nameOffset = r.ReadInt32();
		r.Skip( 8 ); // gatherable text data count and offset
		var exportCount = r.ReadInt32();
		var exportOffset = r.ReadInt32();
		var importCount = r.ReadInt32();
		var importOffset = r.ReadInt32();

		Data = new byte[totalHeaderSize + exports.Length];
		header.AsSpan( 0, Math.Min( header.Length, totalHeaderSize ) ).CopyTo( Data );
		exports.CopyTo( Data, totalHeaderSize );

		// The .uexp ends with the magic again; bulk data offsets count from there.
		BulkDataStart = totalHeaderSize + exports.Length - 4;

		_names = new string[nameCount];
		r.Position = nameOffset;
		for ( var i = 0; i < nameCount; i++ )
		{
			_names[i] = r.ReadFString();
			r.Skip( 4 ); // case-insensitive and case-sensitive hashes
		}

		_imports = new (string, string, int)[importCount];
		r.Position = importOffset;
		for ( var i = 0; i < importCount; i++ )
		{
			ReadName( r ); // class package
			var className = ReadName( r );
			var outer = r.ReadInt32();
			_imports[i] = (className, ReadName( r ), outer);
		}

		var list = new UnrealExport[exportCount];
		r.Position = exportOffset;
		for ( var i = 0; i < exportCount; i++ )
		{
			var classIndex = r.ReadInt32();
			r.Skip( 12 ); // super, template and outer
			var name = ReadName( r );
			r.ReadUInt32(); // object flags
			var size = r.ReadInt64();
			var offset = r.ReadInt64();

			// Forced export, not for client, not for server, package guid, package flags, not always
			// loaded for editor game, is asset, and the five dependency counts.
			r.Skip( 12 + 16 + 4 + 4 + 4 + 20 );
			var className = classIndex < 0 ? _imports[-classIndex - 1].Name : classIndex > 0 ? "export" : "Class";
			list[i] = new UnrealExport( className, name, offset, size );
		}

		Exports = list;
	}

	/// <summary>
	/// A name reference: an index into the name table and a number, which suffixes the name
	/// (<c>_0</c> for 1) when it isn't 0.
	/// </summary>
	public string ReadName( UnrealReader r )
	{
		var index = r.ReadInt32();
		var number = r.ReadInt32();
		var name = index >= 0 && index < _names.Length ? _names[index] : $"<name {index}>";
		return number == 0 ? name : $"{name}_{number - 1}";
	}

	/// <summary>
	/// The name of the object a package index refers to, or null for 0.
	/// </summary>
	public string ObjectName( int index ) =>
		index < 0 && -index <= _imports.Length ? _imports[-index - 1].Name
		: index > 0 && index <= Exports.Count ? Exports[index - 1].Name
		: null;

	/// <summary>
	/// The first export of a class, or null.
	/// </summary>
	public UnrealExport FindExport( string className )
	{
		foreach ( var export in Exports )
		{
			if ( string.Equals( export.Class, className, StringComparison.OrdinalIgnoreCase ) )
				return export;
		}

		return null;
	}

	/// <summary>
	/// Reads an export's tagged properties. Leaves the reader after the terminating "None".
	/// </summary>
	public UnrealProperties ReadProperties( UnrealExport export, out UnrealReader reader )
	{
		reader = new UnrealReader( Data, checked((int)export.SerialOffset) );
		return ReadProperties( reader );
	}

	/// <summary>
	/// Reads tagged properties up to the terminating "None". Values: bool, int, float, string
	/// (names, strings, enum values), an object's name (object properties), lists (arrays), nested
	/// <see cref="UnrealProperties"/> (structs), float arrays (vectors and colors) and byte arrays
	/// (anything else, raw).
	/// </summary>
	public UnrealProperties ReadProperties( UnrealReader r )
	{
		var properties = new UnrealProperties();
		while ( true )
		{
			var name = ReadName( r );
			if ( name == "None" )
				return properties;

			var type = ReadName( r );
			var size = r.ReadInt32();
			var arrayIndex = r.ReadInt32();

			string structName = null, innerType = null;
			var boolValue = false;
			switch ( type )
			{
				case "StructProperty": structName = ReadName( r ); r.Skip( 16 ); break;
				case "BoolProperty": boolValue = r.ReadByte() != 0; break;
				case "ByteProperty":
				case "EnumProperty": ReadName( r ); break;
				case "ArrayProperty":
				case "SetProperty": innerType = ReadName( r ); break;
				case "MapProperty": ReadName( r ); ReadName( r ); break;
			}

			if ( r.ReadByte() != 0 )
				r.Skip( 16 ); // property guid

			var start = r.Position;
			var value = type == "BoolProperty" ? boolValue : ReadValue( r, type, structName, innerType, size );
			r.Position = start + size;
			properties[arrayIndex == 0 ? name : $"{name}[{arrayIndex}]"] = value;
		}
	}

	object ReadValue( UnrealReader r, string type, string structName, string innerType, int size )
	{
		switch ( type )
		{
			case "IntProperty": return r.ReadInt32();
			case "FloatProperty": return r.ReadSingle();
			case "NameProperty": return ReadName( r );
			case "StrProperty": return r.ReadFString();
			case "ObjectProperty": return ObjectName( r.ReadInt32() );
			case "ByteProperty":
			case "EnumProperty": return size == 1 ? r.ReadByte() : ReadName( r );
			case "StructProperty": return ReadStruct( r, structName, size );
			case "ArrayProperty":
			{
				var count = r.ReadInt32();
				var items = new List<object>( count );
				if ( innerType == "StructProperty" )
				{
					// One tag for all the elements, naming their struct.
					ReadName( r );
					ReadName( r );
					var innerSize = r.ReadInt32();
					r.ReadInt32();
					var name = ReadName( r );
					r.Skip( 16 );
					if ( r.ReadByte() != 0 )
						r.Skip( 16 );
					for ( var i = 0; i < count; i++ )
						items.Add( ReadStruct( r, name, count > 0 ? innerSize / count : 0 ) );
				}
				else
				{
					for ( var i = 0; i < count; i++ )
						items.Add( ReadValue( r, innerType, null, null, count > 0 ? (size - 4) / count : 0 ) );
				}

				return items;
			}
			default: return r.ReadBytes( size );
		}
	}

	object ReadStruct( UnrealReader r, string structName, int size )
	{
		switch ( structName )
		{
			case "LinearColor": return new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
			case "Vector": return new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
			case "Vector2D": return new[] { r.ReadSingle(), r.ReadSingle() };
			case "Guid":
			case "IntPoint":
			case "Color":
			case "Box":
			case "Rotator":
			case "Quat":
				return r.ReadBytes( size );
			default: return ReadProperties( r );
		}
	}
}

/// <summary>
/// An object's or struct's tagged properties by name (array elements past the first as <c>Name[i]</c>).
/// </summary>
public sealed class UnrealProperties : Dictionary<string, object>
{
	public T Get<T>( string name ) => TryGetValue( name, out var value ) && value is T t ? t : default;
}
