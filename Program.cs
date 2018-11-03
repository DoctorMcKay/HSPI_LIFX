﻿using System;
using System.Threading;

namespace HSPI_LIFX
{
	public class Program
	{
		private const string DEFAULT_SERVER_ADDRESS = "127.0.0.1";
		private const int DEFAULT_SERVER_PORT = 10400;

		public static void Main(string[] args) {
			string serverAddress = DEFAULT_SERVER_ADDRESS;
			int serverPort = DEFAULT_SERVER_PORT;

			//doLifxThing();
			var client = new LifxClient.Client();
			client.DiscoverDevices();
			
			while (true) {
				Thread.Sleep(250);
			}

			/*
			
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
			}*/
		}
	}
}
