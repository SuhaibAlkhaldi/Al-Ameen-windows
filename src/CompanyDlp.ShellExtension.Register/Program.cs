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

        // DlpPropertySheetHandler's own [Guid(...)] - kept as a literal string (not a reflection
        // lookup off typeof(DlpPropertySheetHandler)) so the association-key write below stays
        // correct even if that attribute is ever read differently by future SharpShell versions.
        private const string PropertySheetHandlerClsid = "{B2E4C7A1-3D5F-4C9E-9A6B-1F8E2D7C4B90}";

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

                    // ServerRegistrationManager.InstallServer registers the CLSID itself (CLSID\{...}
                    // \InprocServer32 etc. - confirmed live, COM activation of the bare CLSID succeeds)
                    // but was confirmed live (2026-08-24, via a full registry search across
                    // HKLM\SOFTWARE\Classes) to NOT write the "*\shellex\PropertySheetHandlers\{CLSID}"
                    // association key that actually tells Explorer to call this handler when showing a
                    // file's Properties dialog - despite reporting success. Without that key, Explorer
                    // has no reason to ever invoke IShellPropSheetExt on this CLSID, so the "DLP" tab
                    // silently never appears for any file, indefinitely, with no error anywhere. Same
                    // category of gap as the Classification column below (SharpShell's registration
                    // manager doesn't cover every registry shape a real shell extension needs) - so
                    // fixed the same way: hand-write the missing key directly, defensively, right after
                    // the library's own registration call.
                    using (var propertySheetHandlersKey = Registry.ClassesRoot.CreateSubKey(
                        $@"*\shellex\PropertySheetHandlers\{PropertySheetHandlerClsid}"))
                    {
                        propertySheetHandlersKey?.SetValue(null, "CompanyDlp Classification");
                    }

                    Console.WriteLine("CompanyDlp.ShellExtension (DLP Properties tab) registered.");

                    // The Explorer "Classification" column is a pure UI convenience read via the
                    // Windows Property System (PSRegisterPropertySchema) - PermissionEvaluator and every
                    // other real enforcement path never consult it, they read classification straight
                    // from FileClassificationCache. A handful of production machines have hit
                    // PSRegisterPropertySchema returning 0x000401A0 (INPLACE_S_TRUNCATED) even after the
                    // PSUnregisterPropertySchema-first fix below - the Windows Property System's own
                    // internal state for this schema GUID appears to get stuck once it has ever
                    // failed/truncated on a machine, and a same-process Unregister+Register pair isn't
                    // always enough to clear that. Letting this failure abort the ENTIRE install (which
                    // also aborts enrollment and starting the service, i.e. actual DLP protection) over
                    // a cosmetic Explorer column is the wrong trade-off - log it and move on instead.
                    try
                    {
                        RegisterClassificationColumn();
                        Console.WriteLine("CompanyDlp.ShellExtension (Classification Explorer column) registered.");
                    }
                    catch (Exception classificationColumnException)
                    {
                        Console.Error.WriteLine(
                            "WARNING: CompanyDlp.ShellExtension (Classification Explorer column) failed to register - " +
                            "continuing without it, since it is a display-only feature and does not affect DLP " +
                            "enforcement. Details: " + classificationColumnException);
                    }
                }
                else
                {
                    ServerRegistrationManager.UninstallServer(handler, registrationType);

                    try
                    {
                        Registry.ClassesRoot.DeleteSubKeyTree(
                            $@"*\shellex\PropertySheetHandlers\{PropertySheetHandlerClsid}", throwOnMissingSubKey: false);
                    }
                    catch { }

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

            // PSRegisterPropertySchema is documented as a one-time-per-install call - re-registering
            // the same schema path (which happens on every run of deploy-agent-portable.ps1, since it
            // wipes and recreates C:\Program Files\CompanyDlp from scratch each time) is unsupported
            // and can itself surface as the same 0x000401A0 (INPLACE_S_TRUNCATED) failure checked
            // below. PSUnregisterPropertySchema first guarantees any stale prior registration for this
            // exact path is gone before re-registering - best-effort, its result is intentionally
            // ignored (there may be nothing registered yet, e.g. on a genuinely first install).
            PSUnregisterPropertySchema(propDescPath);

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
            //
            // Deliberately does NOT throw/return here anymore on a nonzero hr - confirmed live
            // 2026-08-26: this used to `throw` immediately below, which (given the caller only wraps
            // the WHOLE of RegisterClassificationColumn in one try/catch) skipped
            // RegisterPropertyHandlerClsid() and the per-extension PropertyHandler association loop
            // below ENTIRELY on every machine still hitting the still-unresolved 0x000401A0 issue - a
            // full registry search confirmed neither the CLSID nor a single SystemFileAssociations
            // PropertyHandler key existed at all on such a machine, despite the "DLP Classification"
            // column having genuinely appeared and worked in Explorer's column picker on that same
            // machine at an earlier point in time (from a register attempt that happened not to hit
            // the failure). The schema registration and the property-handler COM registration are two
            // independent pieces of Windows infrastructure - a truncated/failed schema registration
            // does not mean the value-computing COM handler can't still be registered and work
            // correctly for whichever properties analysis (if any) is queried via IPropertyStore, so
            // one failing must not block the other. Logged and swallowed instead of thrown; the
            // caller's own try/catch around RegisterClassificationColumn() remains as a second safety
            // net for any other exception this method might still throw (e.g. the FileNotFoundException
            // a few lines up).
            var hr = PSRegisterPropertySchema(propDescPath);
            if (hr != 0)
            {
                Console.Error.WriteLine(
                    $"WARNING: PSRegisterPropertySchema failed for {propDescPath} (HRESULT 0x{hr:X8}) - " +
                    "the column may not be addable via Explorer's column picker on this machine, but " +
                    "continuing to register the property handler itself so classification values are " +
                    "still computed correctly for any property lookup that does reach it.");
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
        //
        // ".dlpenc" is deliberately added on top of that mirrored list (it will never appear in
        // DocumentTextExtractor.SupportedExtensions, since that list is about extracting text from a
        // *readable* file for content classification, and a .dlpenc file's body is opaque ciphertext).
        // Without this, Explorer never calls DlpClassificationPropertyHandler for encrypted files at
        // all, so the "DLP Classification" column - and the whole point of being able to see a file's
        // sensitivity tier before attempting to decrypt it - silently stayed blank for every .dlpenc
        // file, confirmed live 2026-08-25. FileClassificationStatusResolver.ResolveAsync has the
        // matching .dlpenc-aware branch that makes a value actually available once Explorer asks for it.
        private static readonly string[] ClassifiedExtensions = { ".txt", ".pdf", ".docx", ".pptx", ".xlsx", ".jpg", ".jpeg", ".png", ".dlpenc" };

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
