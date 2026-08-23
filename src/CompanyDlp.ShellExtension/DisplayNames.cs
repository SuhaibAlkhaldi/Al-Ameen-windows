using CompanyDlp.Contracts;

namespace CompanyDlp.ShellExtension
{
    // Turns wire codes (FileClassificationStatuses.*, ClassificationTiers.*) into the exact display
    // strings from the feature spec. Kept here, in the shell extension only, since it's pure
    // presentation with no bearing on enforcement - PermissionEvaluator never sees these strings.
    internal static class DisplayNames
    {
        public static string Status(string value)
        {
            switch (value)
            {
                case FileClassificationStatuses.NotScanned: return "Not Scanned";
                case FileClassificationStatuses.Pending: return "Pending";
                case FileClassificationStatuses.Scanning: return "Scanning";
                case FileClassificationStatuses.UpToDate: return "Up to Date";
                case FileClassificationStatuses.ReclassificationRequired: return "Reclassification Required";
                case FileClassificationStatuses.Failed: return "Failed";
                case FileClassificationStatuses.Unsupported: return "Unsupported";
                default: return value;
            }
        }

        public static string Classification(string value)
        {
            switch (value)
            {
                case ClassificationTiers.Public: return "Public";
                // Wire value stays "Internal" (ClassificationTiers.Internal constant unchanged) - only
                // the text shown in the Explorer "Classification" column/property page changed, per
                // explicit request, to "Restricted".
                case ClassificationTiers.Internal: return "Restricted";
                case ClassificationTiers.Secret: return "Secret";
                case ClassificationTiers.VerySecret: return "Very Secret";
                case "Unclassified": return "Unclassified";
                default: return value;
            }
        }
    }
}
