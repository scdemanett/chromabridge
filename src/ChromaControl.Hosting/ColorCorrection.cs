// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Drawing;

namespace ChromaControl.Hosting
{
    /// <summary>
    /// Applies saturation, gamma and brightness to colors
    /// </summary>
    public sealed class ColorCorrection
    {
        /// <summary>
        /// The saturation multiplier
        /// </summary>
        private readonly double _saturation;

        /// <summary>
        /// The gamma and brightness lookup table, one entry per channel value
        /// </summary>
        private readonly byte[] _table;

        /// <summary>
        /// If the correction leaves colors unchanged
        /// </summary>
        public bool IsIdentity { get; }

        /// <summary>
        /// The saturation multiplier in use, after clamping
        /// </summary>
        public double Saturation => _saturation;

        /// <summary>
        /// The gamma in use, after clamping
        /// </summary>
        public double Gamma { get; }

        /// <summary>
        /// The brightness in use, after clamping
        /// </summary>
        public double Brightness { get; }

        /// <summary>
        /// Creates a color correction
        /// </summary>
        /// <param name="options">The lighting options</param>
        public ColorCorrection(LightingOptions options)
        {
            _saturation = Clamp(options.Saturation, 0, 4, 1);
            Gamma = Clamp(options.Gamma, 0.2, 5, 1);
            Brightness = Clamp(options.Brightness, 0, 1, 1);

            _table = new byte[256];

            for (var i = 0; i < _table.Length; i++)
                _table[i] = (byte)Math.Round(Math.Pow(i / 255.0, Gamma) * 255.0 * Brightness);

            IsIdentity = _saturation == 1 && Gamma == 1 && Brightness == 1;
        }

        /// <summary>
        /// Applies the correction to a color
        /// </summary>
        /// <param name="color">The color</param>
        /// <returns>The corrected color</returns>
        public Color Apply(Color color)
        {
            if (IsIdentity)
                return color;

            // Saturation keeps the strongest channel and moves the others away from it,
            // so a color gets deeper without getting dimmer.
            var max = Math.Max(color.R, Math.Max(color.G, color.B));

            return Color.FromArgb(
                _table[Saturate(color.R, max)],
                _table[Saturate(color.G, max)],
                _table[Saturate(color.B, max)]);
        }

        /// <summary>
        /// Applies the saturation multiplier to one channel
        /// </summary>
        /// <param name="channel">The channel value</param>
        /// <param name="max">The strongest channel value of the color</param>
        /// <returns>The channel value</returns>
        private int Saturate(int channel, int max)
        {
            return Math.Clamp((int)Math.Round(max + ((channel - max) * _saturation)), 0, 255);
        }

        /// <summary>
        /// Clamps a setting, falling back when it is not a number
        /// </summary>
        /// <param name="value">The value</param>
        /// <param name="min">The minimum</param>
        /// <param name="max">The maximum</param>
        /// <param name="fallback">The value to use when the setting is not a number</param>
        /// <returns>The clamped value</returns>
        private static double Clamp(double value, double min, double max, double fallback)
        {
            return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        }
    }
}
