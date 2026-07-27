using System;
using SharpShell.ServerRegistration;

namespace CompanyDlp.ShellExtension.Register
{
    // Thin CLI wrapper around SharpShell's ServerRegistrationManager, invoked by
    // scripts\register-shell-extension-production.ps1 / unregister-shell-extension.ps1 (which run
    // elevated, per the same #Requires -RunAsAdministrator convention as every other registration
    // script in this repo - registry writes under HKCR/HKLM require it). Usage:
    //   CompanyDlp.ShellExtension.Register.exe /register
    //   CompanyDlp.ShellExtension.Register.exe /unregister
    internal static class Program
    {
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
                }
                else
                {
                    ServerRegistrationManager.UninstallServer(handler, registrationType);
                    Console.WriteLine("CompanyDlp.ShellExtension (DLP Properties tab) unregistered.");
                }
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Shell extension registration failed: " + exception);
                return 2;
            }
        }
    }
}
