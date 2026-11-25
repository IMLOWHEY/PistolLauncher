using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PistolLauncher;

internal static class SecretStore
{
    private const uint UiForbidden = 0x1;
    private static readonly string SecretDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PistolLauncher");
    private static readonly string KeyPath = Path.Combine(SecretDirectory, "curseforge.key");
    private static readonly string ElyPath = Path.Combine(SecretDirectory, "ely-oauth.dat");

    public static string? Load() => LoadFrom(KeyPath);
    public static void Save(string? secret) => SaveTo(KeyPath, secret, "PistolLauncher CurseForge API key");
    public static string? LoadEly() => LoadFrom(ElyPath);
    public static void SaveEly(string? secret) => SaveTo(ElyPath, secret, "PistolLauncher Ely.by OAuth tokens");

    private static string? LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(path));
            return Encoding.UTF8.GetString(Unprotect(protectedBytes));
        }
        catch { return null; }
    }

    private static void SaveTo(string path, string? secret, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(secret.Trim()), description);
        File.WriteAllText(path, Convert.ToBase64String(protectedBytes));
    }

    private static byte[] Protect(byte[] input, string description) => Transform(input, protect: true, description);
    private static byte[] Unprotect(byte[] input) => Transform(input, protect: false);

    private static byte[] Transform(byte[] input, bool protect, string description = "")
    {
        var inputPtr = Marshal.AllocHGlobal(input.Length);
        Marshal.Copy(input, 0, inputPtr, input.Length);
        var inBlob = new DataBlob { Length = input.Length, Data = inputPtr };
        DataBlob outBlob = default;
        IntPtr descriptionPtr = IntPtr.Zero;
        try
        {
            var ok = protect
                ? CryptProtectData(ref inBlob, description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, out descriptionPtr, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.Length];
            Marshal.Copy(outBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
            if (descriptionPtr != IntPtr.Zero) LocalFree(descriptionPtr);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
