using CompanyDlp.Core;
using Microsoft.Identity.Client;

namespace CompanyDlp.Service;

// Persists OutlookGraphObserver's MSAL token cache to disk, DPAPI-encrypted via MachineDataProtector, so the
// one-time device-code sign-in survives a service restart. Same atomic-write-then-rename pattern as
// FileTransferOutbox/PolicyStore's own file writes.
internal sealed class OutlookGraphTokenCache(MachineDataProtector protector, ILogger logger)
{
    private const string Purpose = "CompanyDlp.OutlookGraphTokenCache.v1";

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanyDlp", "OutlookGraph", "token-cache.bin");

    public void Attach(ITokenCache tokenCache)
    {
        tokenCache.SetBeforeAccess(OnBeforeAccess);
        tokenCache.SetAfterAccess(OnAfterAccess);
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var clearBytes = protector.Unprotect(File.ReadAllBytes(CachePath), Purpose);
            args.TokenCache.DeserializeMsalV3(clearBytes);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load the cached Outlook Graph sign-in; a new sign-in will be required.");
        }
    }

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;
        try
        {
            var protectedBytes = protector.Protect(args.TokenCache.SerializeMsalV3(), Purpose);
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var temporary = CachePath + ".tmp";
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, CachePath, true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not persist the Outlook Graph sign-in; a new sign-in will be required after restart.");
        }
    }
}
