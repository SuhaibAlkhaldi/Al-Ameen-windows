using System;
using System.Runtime.InteropServices;

namespace CompanyDlp.ShellExtension
{
    // Raw COM interop for the Windows Property System - unlike every other extension point in this
    // project (PropertySheet, and previously InfoTip), SharpShell has no wrapper for this one (only
    // SharpIconHandler/SharpIconOverlayHandler/SharpInfoTipHandler/SharpPreviewHandler/
    // SharpThumbnailHandler/SharpPropertySheet exist in SharpShell 2.7.2 - confirmed by reflecting
    // over the assembly, no SharpColumnProvider or IPropertyStore wrapper). So this file hand-declares
    // the exact COM interfaces/structs a Property Handler must implement, matching the Windows SDK
    // headers (propsys.h/shobjidl.h) IIDs verbatim. This is the modern, still-supported replacement
    // for the legacy (Vista-era, effectively unsupported today) IColumnProvider column mechanism.

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY : IEquatable<PROPERTYKEY>
    {
        public Guid fmtid;
        public int pid;

        public PROPERTYKEY(Guid fmtid, int pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }

        public bool Equals(PROPERTYKEY other) => fmtid.Equals(other.fmtid) && pid == other.pid;
        public override bool Equals(object obj) => obj is PROPERTYKEY other && Equals(other);
        public override int GetHashCode() => fmtid.GetHashCode() ^ pid;
    }

    // Minimal PROPVARIANT covering only the scalar cases this handler needs (VT_EMPTY / VT_LPWSTR) -
    // not the full union (VT_VECTOR/VT_ARRAY/VT_DECIMAL members are never produced or consumed here).
    // Layout matches the real PROPVARIANT exactly for the header (vt + 3 reserved WORDs) plus enough
    // trailing bytes for the single-pointer union member LPWSTR occupies - correct and sufficient for
    // any scalar VARTYPE, which is all a read-only "Classification" text property ever needs.
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        private const ushort VT_EMPTY = 0;
        private const ushort VT_LPWSTR = 31;

        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public IntPtr p2; // trailing padding - PROPVARIANT's union is sized for the largest scalar member across x86/x64

        public static PROPVARIANT Empty => new PROPVARIANT { vt = VT_EMPTY };

        public static PROPVARIANT FromString(string value) => new PROPVARIANT
        {
            vt = VT_LPWSTR,
            p = Marshal.StringToCoTaskMemUni(value ?? string.Empty)
        };

        // Must be called by the COM caller (Explorer) via PropVariantClear on every PROPVARIANT it
        // receives from GetValue - this handler never frees the memory itself, matching the standard
        // "callee allocates via CoTaskMem, caller frees via PropVariantClear" COM ownership contract.
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [ComImport]
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    internal static class PropertyKeys
    {
        // CompanyDlp's own Property System format ID - PID starts at 2 (0 and 1 are reserved by the
        // Property System for well-known meanings), per the Windows Property System documentation.
        // Must match CompanyDlp.Classification.propdesc's formatID/propID exactly.
        public static readonly PROPERTYKEY Classification = new PROPERTYKEY(
            new Guid("20219A1A-A3C7-46AF-8822-36EE772CFB2C"), 2);
    }
}
