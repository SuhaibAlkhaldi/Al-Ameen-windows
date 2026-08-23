using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SharpShell.ServerRegistration;

namespace CompanyDlp.ShellExtension.Register
{
    // Thin CLI wrapper around SharpShell's ServerRegistrationManager, invoked by
    // scripts\register-shell-extension-production.ps1 / unregister-shell-extension.ps1 (which run
    // elevated, per the same #Requires -RunAsAdministrator convention as every other registration
    // script in this repo - registry writes under HKCR/HKLM require it). Usage:
    //   CompanyDlp.ShellExtension.Register.exe /register
    //   CompanyDlp.ShellExtension.Register.exe /unregister
    //
    // Also registers/unregisters the "Classification" Explorer column (Windows Property System) -
    // that part does NOT go through ServerRegistrationManager (SharpShell has no concept of a
    // Property Handler), so it's hand-written raw registry/native-API calls instead, same convention
    // this repo already uses everywhere ServerRegistrationManager doesn't apply.
    internal static class Program
    {
        private const string PropertyHandlerClsid = "{803D05F5-7ACD-47BD-B4AB-F89F393C71A6}";

        [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int PSRegisterPropertySchema(string pszPath);

        [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int PSUnregisterPropertySchema(string pszPath);

        private static int Main(string[] args)
        {
            if (args.Length != 1 || (args[0] != "/register" && args[0] != "/unregister"))
            {
                Console.Error.WriteLine("Usage: CompanyDlp.ShellExtension.Register.exe /register|/unregister");
                return 1;
            }

            var handler = new DlpPropertySheetHandler();
            var registrationType = Environment.Is64BitOperatingSystem ? RegistrationType.OS64Bit : RegistrationType.OS32Bit;

            try
            {
                if (args[0] == "/register")
                {
                    ServerRegistrationManager.InstallServer(handler, registrationType, true);
                    Console.WriteLine("CompanyDlp.ShellExtension (DLP Properties tab) registered.");

                    RegisterClassificationColumn();
                    Console.WriteLine("CompanyDlp.ShellExtension (Classification Explorer column) registered.");
                }
                else
                {
                    ServerRegistrationManager.UninstallServer(handler, registrationType);
                    Console.WriteLine("CompanyDlp.ShellExtension (DLP Properties tab) unregistered.");

                    UnregisterClassificationColumn();
                    Console.WriteLine("CompanyDlp.ShellExtension (Classification Explorer column) unregistered.");
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Shell extension registration failed: " + exception);
                return 2;
            }
        }

        // Two independent registrations make the column appear: the property itself (so Windows
        // knows a property named "Classification" exists at all - PSRegisterPropertySchema, reading
        // CompanyDlp.Classification.propdesc, which must be deployed next to this exe), and the COM
        // component that computes its value per file (a hand-written CLSID/InprocServer32 entry,
        // since ServerRegistrationManager only knows how to register SharpShell-derived handler
        // types, not a plain IPropertyStore implementation). "*\PropertyHandler" is the documented
        // wildcard association point for "every file type falls back to this handler" - the same
        // "*" convention DlpPropertySheetHandler already uses via COMServerAssociation(AllFiles).
        private static void RegisterClassificationColumn()
        {
            var propDescPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CompanyDlp.Classification.propdesc");
            if (!File.Exists(propDescPath))
            {
                throw new FileNotFoundException(
                    "CompanyDlp.Classification.propdesc must be deployed next to CompanyDlp.ShellExtension.Register.exe.",
                    propDescPath);
            }

            // PSRegisterPropertySchema is one of the specific Windows APIs where "nonzero but not
            // negative" is still a real failure, not merely informational: 0x000401A0
            // (INPLACE_S_TRUNCATED) has the HRESULT severity bit clear (so FAILED(hr) says
            // "success") but per Microsoft's own documentation means "one or more property
            // descriptions in the schema failed to register" - confirmed live, this exact code came
            // back consistently while CompanyDlp.Classification.propdesc had the wrong xmlns and was
            // missing the required <searchInfo> element, and stopped once both were fixed. Microsoft's
            // docs state plainly that S_OK is the only value indicating full success here, so hr != 0
            // (not the usual FAILED(hr) / hr < 0 convention) is the correct check for this API
            // specifically.
            var hr = PSRegisterPropertySchema(propDescPath);
            if (hr != 0)
            {
                throw new InvalidOperationException(
                    $"PSRegisterPropertySchema failed for {propDescPath} (HRESULT 0x{hr:X8}).");
            }

            RegisterPropertyHandlerClsid();

            // "*\PropertyHandler" (unlike "*\ShellEx\PropertySheetHandlers\{CLSID}", which really is
            // a documented catch-all Explorer's classic shell-extension loader honors) turned out NOT
            // to be a real fallback the Property System's own resolution path consults for ordinary
            // files - confirmed live: registration succeeded with no errors, TreatAsSelf/
            // DisableProcessIsolation were set, yet the column stayed empty for every file and no
            // prevhost.exe surrogate was ever even spawned, meaning Explorer never attempted to
            // activate the handler at all. The actually-documented mechanism is per-extension, via
            // SystemFileAssociations\<ext>\PropertyHandler - so register explicitly for the exact
            // extensions this agent ever produces a real classification for (see
            // DocumentTextExtractor.SupportedExtensions, the single source of truth for that list).
            foreach (var extension in ClassifiedExtensions)
            {
                using (var handlerKey = Registry.ClassesRoot.CreateSubKey(
                    $@"SystemFileAssociations\{extension}\PropertyHandler"))
                {
                    handlerKey?.SetValue(null, PropertyHandlerClsid);
                }
            }
        }

        // Mirrors CompanyDlp.Core.DocumentTextExtractor.SupportedExtensions - not referenced directly
        // because CompanyDlp.Core targets net8.0-windows and this project targets net48 (see
        // CompanyDlp.ShellExtension.csproj's header comment on why), so CompanyDlp.Core isn't
        // available to reference here. Keep in sync by hand if that list ever changes.
        private static readonly string[] ClassifiedExtensions = { ".txt", ".pdf", ".docx", ".jpg", ".jpeg", ".png" };

        private static void UnregisterClassificationColumn()
        {
            // Best-effort throughout: unregistration must clean up as much as it safely can even if
            // an earlier step is already gone (re-running /unregister must never throw).
            foreach (var extension in ClassifiedExtensions)
            {
                try
                {
                    using (var extensionKey = Registry.ClassesRoot.OpenSubKey($@"SystemFileAssociations\{extension}", writable: true))
                    {
                        extensionKey?.DeleteSubKeyTree("PropertyHandler", throwOnMissingSubKey: false);
                    }
                }
                catch { }
            }

            try
            {
                using (var starKey = Registry.ClassesRoot.OpenSubKey("*", writable: true))
                {
                    starKey?.DeleteSubKeyTree("PropertyHandler", throwOnMissingSubKey: false);
                }
            }
            catch { }

            try
            {
                Registry.ClassesRoot.DeleteSubKeyTree(@"CLSID\" + PropertyHandlerClsid, throwOnMissingSubKey: false);
            }
            catch { }

            var propDescPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CompanyDlp.Classification.propdesc");
            if (File.Exists(propDescPath))
            {
                PSUnregisterPropertySchema(propDescPath);
            }
        }

        private static void RegisterPropertyHandlerClsid()
        {
            var handlerType = typeof(DlpClassificationPropertyHandler);
            var assemblyLocation = handlerType.Assembly.Location;
            var codeBase = "file:///" + assemblyLocation.Replace('\\', '/');

            using (var clsidKey = Registry.ClassesRoot.CreateSubKey(@"CLSID\" + PropertyHandlerClsid))
            {
                if (clsidKey == null) throw new InvalidOperationException("Could not create the property handler CLSID registry key.");
                clsidKey.SetValue(null, handlerType.Name);

                // Documented Property Handler requirements (MSDN "Implementing a Property Handler"):
                // without these, Explorer loads property handlers into an isolated surrogate process
                // (prevhost.exe) rather than in-proc - a .NET/mscoree-hosted handler like this one can
                // fail to activate there silently (empty column, no visible error), which matches
                // exactly what was observed after registration otherwise succeeded cleanly.
                clsidKey.SetValue("DisableProcessIsolation", 1, RegistryValueKind.DWord);
                clsidKey.SetValue("TreatAsSelf", 1, RegistryValueKind.DWord);

                using (var inprocKey = clsidKey.CreateSubKey("InprocServer32"))
                {
                    if (inprocKey == null) throw new InvalidOperationException("Could not create the property handler InprocServer32 registry key.");
                    inprocKey.SetValue(null, "mscoree.dll");
                    inprocKey.SetValue("Assembly", handlerType.Assembly.FullName);
                    inprocKey.SetValue("Class", handlerType.FullName);
                    inprocKey.SetValue("RuntimeVersion", "v4.0.30319");
                    inprocKey.SetValue("ThreadingModel", "Both");
                    inprocKey.SetValue("CodeBase", codeBase);

                    // Mirrors the extra version-qualified subkey SharpShell's own ServerRegistrationManager
                    // writes for DlpPropertySheetHandler (confirmed via a live registry query against that
                    // already-working registration) - matching its exact shape rather than only the minimal
                    // keys, since that shape is the one already proven to activate correctly via mscoree.dll.
                    using (var versionedKey = inprocKey.CreateSubKey("0.0.0.0"))
                    {
                        versionedKey?.SetValue("Assembly", handlerType.Assembly.FullName);
                        versionedKey?.SetValue("Class", handlerType.FullName);
                        versionedKey?.SetValue("RuntimeVersion", "v4.0.30319");
                        versionedKey?.SetValue("CodeBase", codeBase);
                    }
                }
            }
        }
    }
}
