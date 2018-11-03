using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace LifxClient
{
	internal class Helpers
	{
		public static string ByteArrayToHexString(byte[] input) {
			StringBuilder hex = new StringBuilder();
			foreach (byte b in input)
			{
				hex.AppendFormat("{0:x2}", b);
			}

			return hex.ToString();
		}

		public static List<IPAddress> GetBroadcastAddresses() {
			List<IPAddress> broadcastAddresses = new List<IPAddress>();
			
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()) {
				if (nic.OperationalStatus != OperationalStatus.Up) {
					continue;
				}

				UnicastIPAddressInformationCollection addrs = nic.GetIPProperties().UnicastAddresses;
				for (byte i = 0; i < addrs.Count; i++) {
					UnicastIPAddressInformation details = addrs[i];
					IPAddress addr = details.Address;
					if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv4MappedToIPv6) {
						// We don't support IPv6 (yet)
						continue;
					}

					byte[] addrBytes = addr.GetAddressBytes();
					byte[] maskBytes = details.IPv4Mask.GetAddressBytes();
					if (addrBytes.Length != maskBytes.Length) {
						// shouldn't happen
						continue;
					}
					
					byte[] broadcastBytes = new byte[addrBytes.Length];
					for (byte j = 0; j < broadcastBytes.Length; j++) {
						broadcastBytes[j] = (byte) (addrBytes[j] | (maskBytes[j] ^ 255));
					}
					
					broadcastAddresses.Add(new IPAddress(broadcastBytes));
				}
			}

			return broadcastAddresses;
		}
	}
}
