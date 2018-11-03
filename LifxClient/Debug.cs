using System;
using System.Runtime.CompilerServices;

namespace LifxClient
{
	public class Debug
	{
		public static event EventHandler<DebugLineEventArgs> LineLogged;
		
		internal static void WriteLine(string message, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null) {
			EventHandler<DebugLineEventArgs> handler = LineLogged;
			if (handler != null) {
				handler(null, new DebugLineEventArgs(message, lineNumber, caller));
			}
		}
	}

	public class DebugLineEventArgs : EventArgs
	{
		public string LogLine { get; private set; }
		public int LineNumber { get; private set; }
		public string CallerName { get; private set; }

		internal DebugLineEventArgs(string msg, int lineNumber, string caller) {
			LogLine = msg;
			LineNumber = lineNumber;
			CallerName = caller;
		}
	}
}
