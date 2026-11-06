// Licensed under the MIT license. See LICENSE.txt in the project root.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChromaBridge.Service.Updates;

/// <summary>
/// Decides whether a downloaded installer is a genuine ChromaBridge release. The service runs
/// the installer as administrator, so this is the check that stops anyone else's code getting
/// that far, including someone who has taken over the GitHub repository.
///
/// A file passes only if both are true:
/// 1. Windows accepts its Authenticode signature (intact file, trusted chain, valid timestamp).
/// 2. The signing certificate carries the publisher's identity code.
///
/// Releases are signed with Azure Artifact Signing, which issues a new certificate every few
/// days, so a thumbprint cannot be pinned. Each certificate does carry an enhanced key usage
/// code that is unique to the verified identity and never changes; that code is what is pinned.
/// </summary>
internal static class AuthenticodeVerifier
{
    /// <summary>
    /// The identity code of the ChromaBridge publisher (Steven Demanett) in Azure Artifact
    /// Signing. A fork that publishes its own releases must replace this with the code from
    /// its own signing certificate.
    /// </summary>
    public const string PublisherIdentityOid = "1.3.6.1.4.1.311.97.311258621.911754533.364183551.14454055";

    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private static readonly Guid s_genericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChainExcludeRoot = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

    /// <summary>
    /// Returns null when the file is a genuine release, or the reason it was refused.
    /// </summary>
    public static string? Verify(string path)
    {
        var trustResult = VerifyTrust(path);

        if (trustResult != 0)
            return $"Windows did not accept the file's signature (0x{trustResult:X8}).";

        X509Certificate2 signer;

        try
        {
            // The one built-in way to read a file's Authenticode signer; the newer
            // certificate loader has no equivalent for signed files
#pragma warning disable SYSLIB0057
            signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException ex)
        {
            return $"The signing certificate could not be read ({ex.Message}).";
        }

        using (signer)
        {
            var usages = signer.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(x => x.EnhancedKeyUsages.Cast<Oid>())
                .Select(x => x.Value)
                .ToHashSet(StringComparer.Ordinal);

            if (!usages.Contains(CodeSigningOid))
                return "The certificate is not a code-signing certificate.";

            if (!usages.Contains(PublisherIdentityOid))
                return $"The file is signed, but not by the ChromaBridge publisher (signed by \"{signer.GetNameInfo(X509NameType.SimpleName, false)}\").";
        }

        return null;
    }

    private static int VerifyTrust(string path)
    {
        var fileInfo = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());

        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var data = new TrustData
            {
                cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = StateActionVerify,
                dwProvFlags = RevocationCheckChainExcludeRoot
            };

            var result = WinVerifyTrust(IntPtr.Zero, s_genericVerifyV2, ref data);

            // Release the state Windows kept for the verification
            data.dwStateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, s_genericVerifyV2, ref data);

            return result;
        }
        finally
        {
            Marshal.DestroyStructure<FileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }
}
