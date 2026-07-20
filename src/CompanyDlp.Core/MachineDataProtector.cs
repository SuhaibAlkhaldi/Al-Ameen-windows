using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CompanyDlp.Core;

public sealed class MachineDataProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;
    private const string DefaultPurpose = "CompanyDlp.AuditOutbox.v1";

    public byte[] Protect(byte[] clearData) => Protect(clearData, DefaultPurpose);
    public byte[] Unprotect(byte[] protectedData) => Unprotect(protectedData, DefaultPurpose);
    public byte[] Protect(byte[] clearData, string purpose) => Transform(clearData, purpose, protect: true);
    public byte[] Unprotect(byte[] protectedData, string purpose) => Transform(protectedData, purpose, protect: false);

    private static byte[] Transform(byte[] input, string purpose, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI is required for the Company DLP audit outbox.");

        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes(purpose ?? DefaultPurpose));
        var inputBlob = CreateBlob(input);
        var entropyBlob = CreateBlob(entropy);
        DataBlob outputBlob = default;
        try
        {
            var flags = CryptProtectUiForbidden | CryptProtectLocalMachine;
            var success = protect
                ? CryptProtectData(ref inputBlob, "Company DLP audit event", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, out outputBlob);

            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(), protect ? "Could not protect audit data." : "Could not unprotect audit data.");

            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, outputBlob.Size);
            return output;
        }
        finally
        {
            FreeBlob(ref inputBlob, false);
            FreeBlob(ref entropyBlob, false);
            FreeBlob(ref outputBlob, true);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        if (data.Length == 0) return default;
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { Size = data.Length, Data = pointer };
    }

    private static void FreeBlob(ref DataBlob blob, bool localFree)
    {
        if (blob.Data == IntPtr.Zero) return;
        if (localFree) LocalFree(blob.Data);
        else Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
