using System.Text;

namespace LifxClient
{
	public class Helpers
	{
		public static string ByteArrayToHexString(byte[] input) {
			StringBuilder hex = new StringBuilder();
			foreach (byte b in input)
			{
				hex.AppendFormat("{0:x2}", b);
			}

			return hex.ToString();
		}
	}
}
