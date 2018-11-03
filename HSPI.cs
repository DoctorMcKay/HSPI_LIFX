using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using HomeSeerAPI;
using Scheduler;
using Scheduler.Classes;

namespace HSPI_LIFX
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : HspiBase
	{
		private LifxClient.Client lifxClient;
		
		private const uint TRANSITION_TIME = 1000; // TODO make this configurable
		
		public HSPI() {
			Name = "LIFX";
			PluginIsFree = true;
		}

		public override string InitIO(string port) {
			Program.WriteLog("verbose", "InitIO");

			hs.RegisterPage("LifxSettings", Name, InstanceFriendlyName());
			var configLink = new HomeSeerAPI.WebPageDesc {
				plugInName = Name,
				link = "LifxSettings",
				linktext = "Settings",
				order = 1,
				page_title = "LIFX Settings",
				plugInInstance = InstanceFriendlyName()
			};
			callbacks.RegisterConfigLink(configLink);
			callbacks.RegisterLink(configLink);

			lifxClient = new LifxClient.Client {DiscoveryFrequency = 10000};
			lifxClient.StartDiscovery();
			lifxClient.DeviceDiscovered += (object source, LifxClient.DeviceEventArgs args) => {
				processDiscoveredDevice(args.Device);
			};
			
			return "";
		}

		public override void SetIOMulti(List<CAPI.CAPIControl> colSend) {
			// TODO inspect all the commands at once
			
			foreach (CAPI.CAPIControl ctrl in colSend) {
				int devRef = ctrl.Ref;
				int controlValue = (int) ctrl.ControlValue;
				string controlString = ctrl.ControlString;
				
				DeviceClass device = (DeviceClass) hs.GetDeviceByRef(devRef);
				string[] addressParts = device.get_Address(hs).Split('-');
				SubDeviceType subType = (SubDeviceType) int.Parse(addressParts[1]);
				Program.WriteLog("debug", "Device ref " + devRef + " (" + addressParts[0] + " " + subType + ") set to " +
				                          controlValue + " (" + controlString + ")");

				int rootRef = hs.DeviceExistsAddress(addressParts[0], false);
				if (rootRef == -1) {
					Program.WriteLog("error", "Root ref does not exist for " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				DeviceBundle bundle = new DeviceBundle(addressParts[0], this) {Root = rootRef};
				bundle.TryFindChildren();
				if (!bundle.IsComplete()) {
					Program.WriteLog("error",
						"No complete device bundle for " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				LifxClient.Device lifxDevice = lifxClient.GetDeviceByAddress(hs3AddressToLifxAddress(addressParts[0]));
				if (lifxDevice == null) {
					Program.WriteLog("error",
						"No known LIFX device for ref " + devRef + " (" + addressParts[0] + ")");
					continue;
				}

				if (subType == SubDeviceType.Brightness && controlValue == 0) {
					lifxDevice.SetPowered(false, TRANSITION_TIME);
					hs.SetDeviceValueByRef(devRef, controlValue, true);
				} else if (subType == SubDeviceType.Brightness && controlValue == 255) {
					lifxDevice.SetPowered(true, TRANSITION_TIME);
					Task.Run(async () => {
						LifxClient.LightStatus status = await lifxDevice.QueryLightStatus();
						hs.SetDeviceValueByRef(devRef, (int) ((double) status.Brightness / ushort.MaxValue * 100), true);
					});
				} else {
					Task.Run(async () => {
						LifxClient.LightStatus status = await lifxDevice.QueryLightStatus();
						HSV color = new HSV {Hue = status.Hue, Saturation = status.Saturation};
						ushort temperature = status.Kelvin;
						ushort brightness = status.Brightness;
						
						switch (subType) {
							case SubDeviceType.Brightness:
								temperature = (ushort) hs.DeviceValue(bundle.Temperature);
								color = ColorConvert.rgbToHsv(
									ColorConvert.stringToRgb(hs.DeviceString(bundle.Color))
								);
								controlValue = Math.Min(controlValue, 100);
								brightness = (ushort) ((double) controlValue / 100.0 * ushort.MaxValue);
								break;
							
							case SubDeviceType.Color:
								color = ColorConvert.rgbToHsv(ColorConvert.stringToRgb(controlString));
								temperature = (ushort) hs.DeviceValue(bundle.Temperature);
								brightness = (ushort) ((double) hs.DeviceValue(bundle.Brightness) / 100.0 * ushort.MaxValue);
								break;
							
							case SubDeviceType.Temperature:
								brightness = (ushort) ((double) hs.DeviceValue(bundle.Brightness) / 100.0 * ushort.MaxValue);
								color = ColorConvert.rgbToHsv(
									ColorConvert.stringToRgb(hs.DeviceString(bundle.Color))
								);
								temperature = (ushort) controlValue;
								break;
						}

						ushort hue = (ushort) (color.Hue * ushort.MaxValue);
						ushort saturation = (ushort) (color.Saturation * ushort.MaxValue);

						if (!status.Powered && brightness > 0) {
							await lifxDevice.SetColorWithAck(hue, saturation, brightness, temperature, 0);
							lifxDevice.SetPowered(true, TRANSITION_TIME);
						} else {
							lifxDevice.SetColor(hue, saturation, brightness, temperature, TRANSITION_TIME);
						}
						
						if (!String.IsNullOrEmpty(controlString)) {
							hs.SetDeviceString(devRef, controlString, true);
						} else {
							hs.SetDeviceValueByRef(devRef, controlValue, true);
						}
					});
				}
			}
		}

		public override string GetPagePlugin(string pageName, string user, int userRights, string queryString) {
			Program.WriteLog("Debug", "Requested page name " + pageName + " by user " + user + " with rights " + userRights);

			switch (pageName) {
				case "LifxSettings":
					return buildSettingsPage(user, userRights, queryString);
			}

			return "";
		}

		private string buildSettingsPage(string user, int userRights, string queryString,
			string messageBox = null, string messageBoxClass = null) {
			
			// TODO
			
			var sb = new StringBuilder();
			sb.Append(PageBuilderAndMenu.clsPageBuilder.DivStart("myq_settings", ""));
			if (messageBox != null) {
				sb.Append("<div" + (messageBoxClass != null ? " class=\"" + messageBoxClass + "\"" : "") + ">");
				sb.Append(messageBox);
				sb.Append("</div>");
			}
			
			sb.Append(PageBuilderAndMenu.clsPageBuilder.FormStart("myq_settings_form",
				"myq_settings_form", "post"));

			sb.Append(@"
<style>
	#myq_settings_form label {
		font-weight: bold;
		text-align: right;
		width: 150px;
		margin-right: 10px;
		display: inline-block;
	}

	#myq_settings_form button {
		margin-left: 163px;
	}

	.myq_message_box {
		padding: 10px;
		border-radius: 10px;
		display: inline-block;
		margin-bottom: 10px;
	}

	.myq_success_message {
		background-color: rgba(50, 255, 50, 0.8);
	}

	.myq_error_message {
		color: white;
		background-color: rgba(255, 50, 50, 0.8);
	}
</style>

<div>
	<label for=""myq_username"">MyQ Email</label>
	<input type=""email"" name=""myq_username"" id=""myq_username"" />
</div>

<div>
	<label for=""myq_password"">MyQ Password</label>
	<input type=""password"" name=""myq_password"" id=""myq_password"" />
</div>

<div>
	<label for=""myq_poll_frequency"">Poll Frequency (ms)</label>
	<input type=""number"" name=""myq_poll_frequency"" id=""myq_poll_frequency"" step=""1"" min=""5000"" />
</div>

<button type=""submit"">Submit</button>
");
			sb.Append(PageBuilderAndMenu.clsPageBuilder.FormEnd());

			var savedSettings = new Dictionary<string, string> {
				{"myq_username", hs.GetINISetting("Authentication", "myq_username", "", IniFilename)},
				//{"myq_password", getMyQPassword(true)},
				{"myq_poll_frequency", hs.GetINISetting("Options", "myq_poll_frequency", "10000", IniFilename)}
			};
			
			sb.Append("<script>var myqSavedSettings = {};");
			sb.Append(@"
for (var i in myqSavedSettings) {
	if (myqSavedSettings.hasOwnProperty(i)) {
		document.getElementById(i).value = myqSavedSettings[i];
	}
}
</script>");
					
			var builder = new PageBuilderAndMenu.clsPageBuilder("LifxSettings");
			builder.reset();
			builder.AddHeader(hs.GetPageHeader("LifxSettings", "LIFX Settings", "", "", false, true));

			builder.AddBody(sb.ToString());
			builder.AddFooter(hs.GetPageFooter());
			builder.suppressDefaultFooter = true;
			
			return builder.BuildPage();
		}

		public override string PostBackProc(string pageName, string data, string user, int userRights) {
			Program.WriteLog("Debug", "PostBackProc for page " + pageName + " with data " + data + " by user " + user + " with rights " + userRights);
			
			/*
			switch (pageName) {
				case "LifxSettings":
					if ((userRights & 2) != 2) {
						// User is not an admin
						return buildSettingsPage(user, userRights, "",
							"Access denied: You are not an administrative user", "lifx_message_box lifx_error_message");
					}

					var authData = new string[] {
						"myq_username",
						"myq_password"
					};
					
					var qs = HttpUtility.ParseQueryString(data);
					var authCredsChanged = false;
					foreach (var key in authData) {
						var oldValue = hs.GetINISetting("Authentication", key, "", IniFilename);
						var newValue = qs.Get(key);
						if (key == "myq_username") {
							newValue = newValue.Trim();
						}

						if (newValue != "*****" && oldValue != newValue) {
							authCredsChanged = true;
						}
					}

					if (authCredsChanged) {
						var username = qs.Get("myq_username").Trim();
						var password = qs.Get("myq_password");
						
						hs.SaveINISetting("Authentication", "myq_username", username.Trim(), IniFilename);
						if (password != "*****") {
							// This doesn't provide any actual security, but at least the password isn't in
							// plaintext on the disk. Base64 is juuuuust barely not plaintext, but what're ya
							// gonna do?
							var encoded = System.Convert.ToBase64String(Encoding.UTF8.GetBytes((string) password));
							hs.SaveINISetting("Authentication", "myq_password", encoded, IniFilename);							
						}
					}

					var pollFrequency = qs.Get("myq_poll_frequency");
					int n;
					if (pollFrequency != null && int.TryParse(pollFrequency, out n) && n >= 5000) {
						hs.SaveINISetting("Options", "myq_poll_frequency", pollFrequency, IniFilename);
						pollTimer.Interval = n;
					}

					if (authCredsChanged) {
						var authTask = myqClient.login(hs.GetINISetting("Authentication", "myq_username", "", IniFilename),
							getMyQPassword(false), true);
						authTask.Wait();
						if (authTask.Result.Length > 0) {
							return buildSettingsPage(user, userRights, "", authTask.Result,
								"myq_message_box myq_error_message");
						}
						else {
							syncDevices();
							return buildSettingsPage(user, userRights, "",
								"Settings have been saved successfully. Authentication success.",
								"myq_message_box myq_success_message");
						}
					}
					else {
						return buildSettingsPage(user, userRights, "", "Settings have been saved successfully.",
							"myq_message_box myq_success_message");
					}
			}
			*/
			
			return "";
		}

		private void processDiscoveredDevice(LifxClient.Device lifxDevice) {
			string hs3Addr = lifxAddressToHs3Address(lifxDevice.Address);
			Program.WriteLog("debug", "Discovered LIFX device " + hs3Addr + " at " + lifxDevice.IPAddress);
			
			// Do we already have an HS3 device for this?
			DeviceBundle bundle = new DeviceBundle(hs3Addr, this);

			int devRef = hs.DeviceExistsAddress(bundle.GetSubDeviceAddress(SubDeviceType.Root), false);
			bundle.Root = devRef == -1 ? 0 : devRef;
			bundle.TryFindChildren();

			if (bundle.IsComplete()) {
				Program.WriteLog("info", "Complete device bundle found in HS3 for LIFX device " + hs3Addr);
			} else {
				bundle.CreateDevices(lifxDevice.LastKnownStatus.Label);
			}
		}
		
		private string lifxAddressToHs3Address(ulong lifxAddress) {
			string addressHex = lifxAddress.ToString("X12");
			StringBuilder builder = new StringBuilder();

			for (int i = 5; i >= 0; i--) {
				builder.Append(addressHex.Substring(i * 2, 2));
			}

			return builder.ToString().ToUpper();
		}

		private ulong hs3AddressToLifxAddress(string hs3Address) {
			StringBuilder builder = new StringBuilder();
			for (int i = 5; i >= 0; i--) {
				builder.Append(hs3Address.Substring(i * 2, 2));
			}

			return ulong.Parse(builder.ToString(), NumberStyles.HexNumber);
		}
	}
}
