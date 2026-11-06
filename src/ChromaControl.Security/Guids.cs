// Licensed to the Chroma Control Contributors under one or more agreements.
// The Chroma Control Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace ChromaControl.Security
{
    /// <summary>
    /// Secure GUIDs class
    /// </summary>
    public static class Guids
    {
        /// <summary>
        /// The data file name
        /// </summary>
        private const string DataFileName = "ChromaControl.dat";

        /// <summary>
        /// Gets a secure GUID
        /// </summary>
        /// <param name="name">The name of the guid to get</param>
        /// <returns>The guid</returns>
        public static Guid GetSecureGuid(string name)
        {
            var guids = new Dictionary<string, Guid>();

            using var fileStream = File.OpenRead(FindDataFile());
            using var aes = Aes.Create();

            var key = new byte[32];
            var iv = new byte[16];

            fileStream.ReadExactly(key);
            fileStream.ReadExactly(iv);

            using var cryptoStream = new CryptoStream(fileStream, aes.CreateDecryptor(key, iv), CryptoStreamMode.Read);
            using var binReader = new BinaryReader(cryptoStream);

            var count = binReader.ReadInt32();

            for (var i = 0; i < count; i++)
                guids.Add(binReader.ReadString(), Guid.Parse(binReader.ReadString()));

            return guids[name];
        }

        /// <summary>
        /// Finds the data file, which sits in a Data folder beside the module (unpackaged build)
        /// or one level up at the package root (packaged install)
        /// </summary>
        /// <returns>The data file path</returns>
        private static string FindDataFile()
        {
            var baseDirectory = AppContext.BaseDirectory;

            var candidates = new[]
            {
                Path.Combine(baseDirectory, "Data", DataFileName),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "Data", DataFileName))
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException($"Could not find {DataFileName}. Looked in: {string.Join("; ", candidates)}");
        }
    }
}
