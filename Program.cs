﻿using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HSPI_LIFX
{
	public class Program
	{
		public static HomeSeerAPI.IHSApplication HsClient;
		
		private const string DEFAULT_SERVER_ADDRESS = "127.0.0.1";
		private const int DEFAULT_SERVER_PORT = 10400;

		public static void Main(string[] args) {
			string serverAddress = DEFAULT_SERVER_ADDRESS;
			int serverPort = DEFAULT_SERVER_PORT;

			LifxClient.Debug.LineLogged += (object sender, LifxClient.DebugLineEventArgs eventArgs) => {
				if (!eventArgs.LogLine.Contains("Type = StateService")) {
					WriteLog("verbose", "[LIFX] " + eventArgs.LogLine, eventArgs.LineNumber, eventArgs.CallerName);
				}
			};
			
			foreach (string arg in args) {
				string[] parts = arg.Split('=');
				switch (parts[0].ToLower()) {
					case "server":
						serverAddress = parts[1];
						break;
					
					default:
						Console.WriteLine("Warning: Unknown command line argument " + parts[0]);
						break;
				}
			}

			HSPI plugin = new HSPI();
			Console.WriteLine("Plugin " + plugin.Name + " is connecting to HS3 at " + serverAddress + ":" + serverPort);
			try {
				plugin.Connect(serverAddress, serverPort);
				Console.WriteLine("Connection established");
			}
			catch (Exception ex) {
				Console.WriteLine("Unable to connect to HS3: " + ex.Message);
				return;
			}

			try {
				while (true) {
					System.Threading.Thread.Sleep(250);
					if (!plugin.Connected) {
						Console.WriteLine("Connection to HS3 lost!");
						break;
					}

					if (plugin.Shutdown) {
						Console.WriteLine("Plugin has been shut down; exiting");
						break;
					}
				}
			}
			catch (Exception ex) {
				Console.WriteLine("Unhandled exception: " + ex.Message);
			}
		}
		
		public static void WriteLog(string type, string message, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null) {
			type = type.ToLower();

			// Don't log Silly type messages to the log unless this is a debug build
#if DEBUG
			if (type != "console") {
				HsClient.WriteLog(type == "silly" ? "LIFX Silly" : "LIFX",
					type + ": [" + caller + ":" + lineNumber + "] " + message);
			}

			System.Console.WriteLine("[" + type + "] " + message);
#else
			if (type != "verbose" && type != "silly" && type != "console") {
				HsClient.WriteLog("LIFX", type + ": " + message);
			}
#endif
		}
	}
}
