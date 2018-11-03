using System;
using System.Globalization;

namespace HSPI_LIFX
{
	public class ColorConvert
	{
		public static RGB stringToRgb(string input) {
			return new RGB {
				Red = byte.Parse(input.Substring(0, 2), NumberStyles.HexNumber),
				Green = byte.Parse(input.Substring(2, 2), NumberStyles.HexNumber),
				Blue = byte.Parse(input.Substring(4, 2), NumberStyles.HexNumber),
			};
		}
		
		public static HSV rgbToHsv(RGB input) {
			double r = input.Red / 255.0;
			double g = input.Green / 255.0;
			double b = input.Blue / 255.0;

			double min = Math.Min(Math.Min(r, g), b);
			double max = Math.Max(Math.Max(r, g), b);
			double deltaMax = max - min;

			HSV output = new HSV();
			output.Value = max;

			if (doubleEquals(deltaMax, 0)) {
				// Gray (which is white)
				output.Hue = 0;
				output.Saturation = 0;
			} else {
				output.Saturation = deltaMax / max;

				double deltaR = ((max - r) / 6 + deltaMax / 2) / deltaMax;
				double deltaG = ((max - g) / 6 + deltaMax / 2) / deltaMax;
				double deltaB = ((max - b) / 6 + deltaMax / 2) / deltaMax;

				if (doubleEquals(r, max)) {
					output.Hue = deltaB - deltaG;
				} else if (doubleEquals(g, max)) {
					output.Hue = (1.0 / 3.0) + deltaR - deltaB;
				} else if (doubleEquals(b, max)) {
					output.Hue = (2.0 / 3.0) + deltaG - deltaR;
				}

				if (output.Hue < 0) {
					output.Hue += 1;
				}

				if (output.Hue > 1) {
					output.Hue -= 1;
				}
			}

			return output;
		}

		private static bool doubleEquals(double a, double b) {
			return Math.Abs(a - b) < 0.0000001;
		}
	}

	public class RGB
	{
		public byte Red { get; set; }
		public byte Green { get; set; }
		public byte Blue { get; set; }
	}

	public class HSV
	{
		public double Hue { get; set; }
		public double Saturation { get; set; }
		public double Value { get; set; }
	}
}