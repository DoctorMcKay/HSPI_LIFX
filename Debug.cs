using System;

namespace HSPI_LIFX
{
	public class Debug
	{
		public static void WriteLine(string msg) {
#if DEBUG
			Console.WriteLine(msg);
#endif
		}
	}
}
