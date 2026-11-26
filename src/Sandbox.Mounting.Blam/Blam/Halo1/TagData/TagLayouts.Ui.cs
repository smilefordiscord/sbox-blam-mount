namespace Blam.Halo1.TagData;

public static partial class TagLayouts
{
	static readonly string[] UiEventType =
	[
		"a button", "b button", "x button", "y button", "black button", "white button", "left trigger", "right trigger",
		"dpad up", "dpad down", "dpad left", "dpad right", "start button", "back button", "left thumb", "right thumb",
		"left analog stick up", "left analog stick down", "left analog stick left", "left analog stick right",
		"right analog stick up", "right analog stick down", "right analog stick left", "right analog stick right",
		"created", "deleted", "get focus", "lose focus", "left mouse", "middle mouse", "right mouse", "double click",
		"custom activation", "post render",
	];

	static readonly TagStruct UiEventHandler = new TagStruct( "event handler reference", 0x48 )
		.Flags32( 0x0, "flags", ["close current widget", "close other widget", "close all widgets", "open widget", "reload self", "reload other widget", "give focus to widget", "run function", "replace self w widget", "go back to previous widget", "run scenario script", "try to branch on failure"] )
		.Enum( 0x4, "event type", UiEventType )
		.Int16( 0x6, "function" )
		.Reference( 0x8, "widget tag" )
		.Reference( 0x18, "sound effect" )
		.String( 0x28, "script" );

	static readonly TagStruct UiSearchAndReplace = new TagStruct( "search and replace reference", 0x22 )
		.String( 0x0, "search string" )
		.Enum( 0x20, "replace function", ["null", "widget s controller", "build number", "pid"] );

	static readonly TagStruct UiChildWidget = new TagStruct( "child widget reference", 0x50 )
		.Reference( 0x0, "widget tag" )
		.String( 0x10, "name" )
		.Flags32( 0x30, "flags", ["use custom controller index"] )
		.Index( 0x34, "custom controller index" )
		.Int16( 0x36, "vertical offset" )
		.Int16( 0x38, "horizontal offset" );

	/// <summary>
	/// <c>DeLa</c>, 0x3EC bytes: a UI widget (a menu, a dialog, a text box in one) with its
	/// bounds in the 640x480 screen, background bitmap, text, event handlers and child widgets.
	/// The game data inputs, list header and footer and conditional widgets are left out.
	/// </summary>
	public static readonly TagStruct UiWidget = new TagStruct( "ui widget definition", 0x3EC )
		.Enum( 0x0, "widget type", ["container", "text box", "spinner list", "column list", "game model not implemented", "movie not implemented", "custom not implemented"] )
		.Enum( 0x2, "controller index", ["player 1", "player 2", "player 3", "player 4", "any player"] )
		.String( 0x4, "name" )
		.Rectangle2D( 0x24, "bounds" )
		.Flags32( 0x2C, "flags", ["pass unhandled events to focused child", "pause game time", "flash background bitmap", "dpad up down tabs thru children", "dpad left right tabs thru children", "dpad up down tabs thru list items", "dpad left right tabs thru list items", "dont focus a specific child widget", "pass unhandled events to all children", "render regardless of controller index", "pass handled events to all children", "return to main menu if no history", "always use tag controller index", "always use nifty render fx", "don't push history", "force handle mouse"] )
		.Int32( 0x30, "milliseconds to auto close" )
		.Int32( 0x34, "milliseconds auto close fade time" )
		.Reference( 0x38, "background bitmap" )
		.Block( 0x54, "event handlers", UiEventHandler )
		.Block( 0x60, "search and replace functions", UiSearchAndReplace )
		.Reference( 0xEC, "text label unicode strings list" )
		.Reference( 0xFC, "text font" )
		.ColorArgb( 0x10C, "text color" )
		.Enum( 0x11C, "justification", ["left justify", "right justify", "center justify"] )
		.Flags32( 0x11E, "flags 1", ["editable", "password", "flashing", "don't do that weird focus test"] )
		.Index( 0x12E, "string list index" )
		.Int16( 0x130, "horiz offset" )
		.Int16( 0x132, "vert offset" )
		.Block( 0x3E0, "child widgets", UiChildWidget );
}
