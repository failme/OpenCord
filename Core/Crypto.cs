using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeScord;

// DPAPI, straight off crypt32 — the native Windows "protect a secret for this user" API.
//
// Used to store the account token at rest. A user token is full account access, so writing it to
// prefs.json in plain text would mean anyone who copied that file had the account. CurrentUser-scope
// DPAPI ties the ciphertext to this Windows login, so a copied prefs.json is inert elsewhere.
//
// P/Invoke rather than the System.Security.Cryptography.ProtectedData package: it is the same OS
// call underneath, needs no dependency, and the round trip is covered in SelfTest.
static class Crypto
{
    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr hMem);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;   // never pop a UI prompt, even if the OS wants to

    public static string Protect(string plain) => Convert.ToBase64String(Run(CryptProtectData, Encoding.UTF8.GetBytes(plain)));

    /// Returns null rather than throwing when the blob is not ours to read (wrong user, tampered,
    /// or just not a DPAPI blob) — the caller treats that as "no saved token".
    public static string? TryUnprotect(string base64)
    {
        try { return Encoding.UTF8.GetString(Run(CryptUnprotectDataAdapter, Convert.FromBase64String(base64))); }
        catch { return null; }
    }

    // CryptUnprotectData has one fewer parameter (no description); adapt it to the same delegate shape.
    static bool CryptUnprotectDataAdapter(ref DATA_BLOB pIn, string? _, IntPtr entropy, IntPtr r1, IntPtr r2, int flags, ref DATA_BLOB pOut) =>
        CryptUnprotectData(ref pIn, IntPtr.Zero, entropy, r1, r2, flags, ref pOut);

    delegate bool Op(ref DATA_BLOB pIn, string? desc, IntPtr entropy, IntPtr r1, IntPtr r2, int flags, ref DATA_BLOB pOut);

    static byte[] Run(Op op, byte[] input)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = pin.AddrOfPinnedObject();
            if (!op(ref inBlob, "ClaudeScord token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return outBytes;
        }
        finally
        {
            pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
