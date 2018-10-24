namespace HSPI_LIFX
{
	public class LifxDeviceDiscoveredEventArgs
	{
		public LifxDevice Device { get; set; }

		public LifxDeviceDiscoveredEventArgs(LifxDevice dev) {
			Device = dev;
		}
	}
}
