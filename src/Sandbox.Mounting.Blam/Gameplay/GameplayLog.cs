// The gameplay code's log, under the mount's name; mount code keeps its own loggers.
internal static class Log
{
	static readonly Sandbox.Diagnostics.Logger Logger = new( "halo" );

	public static void Info( object message ) => Logger.Info( message );
	public static void Warning( object message ) => Logger.Warning( message );
	public static void Warning( Exception exception, object message ) => Logger.Warning( exception, message );
}
