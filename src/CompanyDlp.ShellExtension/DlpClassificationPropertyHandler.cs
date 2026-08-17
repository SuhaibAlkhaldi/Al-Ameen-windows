using System;
using System.Runtime.InteropServices;

namespace CompanyDlp.ShellExtension
{
    // Supplies the "Classification" Explorer column's value per file, via the Windows Property
    // System - the modern, currently-supported replacement for the legacy (effectively unmaintained
    // since Vista) IColumnProvider mechanism. Registered against CompanyDlp.Classification.propdesc's
    // property (see PropertySystemInterop.PropertyKeys.Classification) and associated with all file
    // types (see CompanyDlp.ShellExtension.Register\Program.cs for the exact registry keys).
    //
    // Same data source and same "don't trust a non-Up-to-Date classification" safety rule as the
    // "Classification" Properties-dialog tab (DlpPropertySheetHandler/DlpPropertyPage) - that rule
    // lives entirely server-side in FileClassificationStatusResolver.cs, so response.Classification
    // is already safe to display verbatim here with no duplicated filtering logic.
    //
    // Like StatusPipeClient's own header explains: this type is loaded in-proc by explorer.exe via
    // mscoree.dll, so it must never let an exception escape a COM entry point - Explorer would treat
    // an unhandled exception from a property handler as reason to stop showing the column for that
    // file (or worse), rather than a controlled "no value available" result.
    [ComVisible(true)]
    [Guid("803D05F5-7ACD-47BD-B4AB-F89F393C71A6")]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("CompanyDlp.ClassificationPropertyHandler")]
    public sealed class DlpClassificationPropertyHandler : IPropertyStore, IInitializeWithFile
    {
        private string? _filePath;

        public void Initialize(string pszFilePath, uint grfMode)
        {
            _filePath = pszFilePath;
        }

        public void GetCount(out uint cProps)
        {
            cProps = 1;
        }

        public void GetAt(uint iProp, out PROPERTYKEY pkey)
        {
            pkey = PropertyKeys.Classification;
        }

        public void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv)
        {
            try
            {
                var filePath = _filePath;
                if (!key.Equals(PropertyKeys.Classification) || string.IsNullOrWhiteSpace(filePath))
                {
                    pv = PROPVARIANT.Empty;
                    return;
                }

                // net48's BCL isn't nullable-annotated (unlike modern .NET), so the compiler can't
                // see that IsNullOrWhiteSpace already ruled out null above - the ! is provably safe,
                // not a suppression of a real gap.
                var response = StatusPipeClient.Query(filePath!);
                var text = response is null
                    ? "Unavailable"
                    : DisplayNames.Classification(response.Classification);
                pv = PROPVARIANT.FromString(text);
            }
            catch
            {
                // Never let an exception escape a COM entry point loaded in-proc by explorer.exe -
                // degrade to "no value" rather than risk destabilizing Explorer for every row.
                pv = PROPVARIANT.Empty;
            }
        }

        // Read-only property - Explorer has no reason to ever call this (nothing in our column UI
        // lets a user edit the value), but a well-behaved IPropertyStore must not throw from it.
        public void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv)
        {
        }

        public void Commit()
        {
        }
    }
}
