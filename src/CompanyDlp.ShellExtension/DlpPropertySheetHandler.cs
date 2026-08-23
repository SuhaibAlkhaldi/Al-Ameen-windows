using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpShell.Attributes;
using SharpShell.SharpPropertySheet;

namespace CompanyDlp.ShellExtension
{
    // Adds a "DLP" tab to the file Properties dialog (right-click a file -> Properties) showing its
    // Classification, Status, and Last Scanned time - sourced from the DLP agent's background
    // classification pipeline via the getFileClassificationStatus pipe message (see
    // CompanyDlp.Service\FileClassificationStatusResolver.cs, which is also the only place the
    // "don't trust a non-Up-to-Date classification" display rule lives - this class is a pure
    // formatter, same as the abandoned InfoTip attempt, just via a different, strictly-additive
    // Windows shell extension point: a property sheet page only ever adds a new tab, it can never
    // replace or suppress any of Explorer's own existing UI (unlike IQueryInfo/InfoTip, which was
    // proven - via an isolated, narrowly-scoped test - to replace the default tooltip outright).
    [ComVisible(true)]
    [Guid("B2E4C7A1-3D5F-4C9E-9A6B-1F8E2D7C4B90")]
    [COMServerAssociation(AssociationType.AllFiles)]
    [RegistrationName("CompanyDlp Classification")]
    public class DlpPropertySheetHandler : SharpPropertySheet
    {
        protected override bool CanShowSheet() => true;

        protected override IEnumerable<SharpPropertyPage> CreatePages()
        {
            // Built as a plain list, not via yield return - a page-creation failure here must
            // never throw out of this COM entry point (this is loaded in-proc by explorer.exe
            // whenever a Properties dialog opens), and C# doesn't allow yield inside a try/catch.
            try
            {
                var path = SelectedItemPaths?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new List<SharpPropertyPage> { new DlpPropertyPage("no file was selected.") };
                }

                var response = StatusPipeClient.Query(path!);
                return new List<SharpPropertyPage>
                {
                    response is not null
                        ? new DlpPropertyPage(response)
                        : new DlpPropertyPage("the Al-Ameen agent is not reachable right now.")
                };
            }
            catch (System.Exception exception)
            {
                // Full exception detail (including inner exceptions, which is where a
                // FileNotFoundException usually names the actual missing assembly) is genuinely
                // useful while diagnosing a broken install - the tab's own space is too cramped to
                // show a full stack trace, and this avoids a round trip through Event Viewer - but
                // it can include file paths, so it's off by default in shipped builds. Set
                // COMPANY_DLP_SHELLEXTENSION_DIAGNOSTICS=1 in the environment to enable it.
                if (System.Environment.GetEnvironmentVariable("COMPANY_DLP_SHELLEXTENSION_DIAGNOSTICS") == "1")
                {
                    try
                    {
                        System.IO.File.WriteAllText(
                            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CompanyDlp.ShellExtension.error.log"),
                            System.DateTime.Now + "\r\n" + exception);
                    }
                    catch { }
                }

                return new List<SharpPropertyPage> { new DlpPropertyPage(exception.GetType().Name + ": " + exception.Message) };
            }
        }
    }
}
