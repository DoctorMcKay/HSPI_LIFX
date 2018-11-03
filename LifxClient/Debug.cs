using System;

namespace LifxClient
{
	public class Debug
	{
		public static event EventHandler<DebugLineEventArgs> LineLogged;
		
		public static void WriteLine(string message) {
			EventHandler<DebugLineEventArgs> handler = LineLogged;
			if (handler != null) {
				handler(null, new DebugLineEventArgs(message));
			}
		}
	}

	public class DebugLineEventArgs : EventArgs
	{
		public string LogLine { get; private set; }

		public DebugLineEventArgs(string msg) {
			LogLine = msg;
		}
	}
}
