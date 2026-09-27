using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LiveCaptionsTranslator.utils;

/// <summary>Protects API secrets with Windows DPAPI for the current user.</summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private const int CryptProtectUiForbidden = 0x1;

    public static bool NeedsMigration { get; private set; }
    public static bool DecryptionFailed { get; private set; }
    public static string? CleanupWarning { get; private set; }
    public static string? PersistenceWarning { get; private set; }
    public static event Action? StatusChanged;

    public static void ResetLoadStatus()
    {
        NeedsMigration = false;
        DecryptionFailed = false;
        CleanupWarning = null;
        PersistenceWarning = null;
        NotifyStatusChanged();
    }

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("A protected secret was passed to the settings writer as plaintext.");

        byte[] inputBytes = Encoding.UTF8.GetBytes(value);
        var input = new DataBlob(inputBytes);
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out DataBlob output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] protectedBytes = new byte[output.cbData];
                Marshal.Copy(output.pbData, protectedBytes, 0, output.cbData);
                try { return Prefix + Convert.ToBase64String(protectedBytes); }
                finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            }
            finally
            {
                if (output.pbData != IntPtr.Zero)
                    LocalFree(output.pbData);
            }
        }
        finally
        {
            ZeroAndFreeHGlobal(input);
            CryptographicOperations.ZeroMemory(inputBytes);
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (!NeedsMigration)
            {
                NeedsMigration = true;
                NotifyStatusChanged();
            }
            return value;
        }

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
            var input = new DataBlob(protectedBytes);
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out DataBlob output))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    byte[] clearBytes = new byte[output.cbData];
                    Marshal.Copy(output.pbData, clearBytes, 0, output.cbData);
                    try { return Encoding.UTF8.GetString(clearBytes); }
                    finally { CryptographicOperations.ZeroMemory(clearBytes); }
                }
                finally
                {
                    if (output.pbData != IntPtr.Zero)
                        ZeroAndLocalFree(output);
                }
            }
            finally
            {
                ZeroAndFreeHGlobal(input);
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or Win32Exception or ArgumentException)
        {
            if (!DecryptionFailed)
            {
                DecryptionFailed = true;
                NotifyStatusChanged();
            }
            return string.Empty;
        }
    }

    public static void SetCleanupWarning(string path)
    {
        CleanupWarning = path;
        NotifyStatusChanged();
    }

    public static void SetPersistenceWarning(string path)
    {
        PersistenceWarning = path;
        NotifyStatusChanged();
    }

    public static void ClearPersistenceWarning()
    {
        if (PersistenceWarning == null)
            return;
        PersistenceWarning = null;
        NotifyStatusChanged();
    }

    private static void NotifyStatusChanged()
    {
        Action? handlers = StatusChanged;
        if (handlers == null)
            return;

        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { }
        }
    }

    private static void ZeroAndFreeHGlobal(DataBlob blob)
    {
        if (blob.pbData == IntPtr.Zero)
            return;

        try { ZeroUnmanagedBuffer(blob.pbData, blob.cbData); }
        finally { Marshal.FreeHGlobal(blob.pbData); }
    }

    private static void ZeroAndLocalFree(DataBlob blob)
    {
        try { ZeroUnmanagedBuffer(blob.pbData, blob.cbData); }
        finally { LocalFree(blob.pbData); }
    }

    private static void ZeroUnmanagedBuffer(IntPtr pointer, int length)
    {
        for (int offset = 0; offset < length; offset++)
            Marshal.WriteByte(pointer, offset, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;

        public DataBlob(byte[] bytes)
        {
            cbData = bytes.Length;
            pbData = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pbData, bytes.Length);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
