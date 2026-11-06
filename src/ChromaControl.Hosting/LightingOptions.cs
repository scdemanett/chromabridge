// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace ChromaControl.Hosting
{
    /// <summary>
    /// Color correction applied to Chroma colors before they are sent to devices.
    /// Read from the "Lighting" section of appsettings.json; edits apply without a restart.
    /// </summary>
    public class LightingOptions
    {
        /// <summary>
        /// Saturation multiplier. 1 leaves colors unchanged, higher values deepen them,
        /// 0 turns them gray.
        /// </summary>
        public double Saturation { get; set; } = 1.0;

        /// <summary>
        /// Gamma applied to each channel. 1 leaves colors unchanged. LEDs are close to linear,
        /// so values around 2 stop mixed colors looking washed out.
        /// </summary>
        public double Gamma { get; set; } = 1.0;

        /// <summary>
        /// Brightness multiplier from 0 to 1
        /// </summary>
        public double Brightness { get; set; } = 1.0;

        /// <summary>
        /// Names of devices to leave alone, as they appear in the log (for example an
        /// addressable header with nothing plugged in). Read when the module starts.
        /// </summary>
        public string[] ExcludedDevices { get; set; } = System.Array.Empty<string>();
    }
}
