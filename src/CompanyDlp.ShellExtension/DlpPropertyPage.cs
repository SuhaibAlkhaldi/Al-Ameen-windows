using System.Drawing;
using System.Windows.Forms;
using CompanyDlp.Contracts;

namespace CompanyDlp.ShellExtension
{
    // The visual content of the "DLP" tab added to the file Properties dialog. Built
    // programmatically (no .Designer.cs) since it's a handful of static labels, not an interactive
    // form. Purely a formatter: DlpPropertySheetHandler already resolved the status/classification
    // before constructing this page, so there is no pipe/IO code here at all.
    public sealed class DlpPropertyPage : SharpShell.SharpPropertySheet.SharpPropertyPage
    {
        public DlpPropertyPage(FileClassificationStatusResponse response)
        {
            PageTitle = "Classification";
            BuildLayout(response);
        }

        public DlpPropertyPage(string reasonUnavailable)
        {
            PageTitle = "Classification";
            BuildUnavailableLayout(reasonUnavailable);
        }

        private void BuildLayout(FileClassificationStatusResponse response)
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(12),
                AutoSize = true
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(table, 0, "Classification:", DisplayNames.Classification(response.Classification));
            AddRow(table, 1, "Status:", DisplayNames.Status(response.Status));
            AddRow(table, 2, "Last Scanned:",
                response.LastScannedAtUtc.HasValue ? response.LastScannedAtUtc.Value.ToLocalTime().ToString("g") : "Not available");

            Controls.Add(table);
        }

        private void BuildUnavailableLayout(string reason)
        {
            var label = new Label
            {
                Text = "DLP classification is unavailable: " + reason,
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(12),
                ForeColor = Color.DarkRed
            };
            Controls.Add(label);
        }

        private static void AddRow(TableLayoutPanel table, int row, string labelText, string valueText)
        {
            table.Controls.Add(new Label { Text = labelText, AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 4, 8, 4) }, 0, row);
            table.Controls.Add(new Label { Text = valueText, AutoSize = true, Margin = new Padding(0, 4, 0, 4) }, 1, row);
        }
    }
}
