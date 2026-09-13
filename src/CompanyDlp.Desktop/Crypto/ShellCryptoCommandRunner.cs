using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;
using CompanyDlp.Contracts;
using CompanyDlp.Desktop.Services;

namespace CompanyDlp.Desktop.Crypto;

public sealed record ShellCryptoCommand(string Action, string FilePath);

public static class ShellCryptoCommandRunner
{
    private const string EncryptAction = "--encrypt-and-delete";
    private const string DecryptAction = "--decrypt";

    // Reached via the .dlpenc ProgID's default "open" command (see
    // scripts\register-development-context-menu.ps1) - the front door for a double-clicked .dlpenc
    // file, distinct from the explicit right-click --decrypt verb. See
    // FileProtectionCoordinator.ExecuteOpenAccessAsync's comment for what this actually gates.
    private const string RequestAccessAction = "--request-access";

    public static bool TryParse(IReadOnlyList<string> args, out ShellCryptoCommand command)
    {
        command = new ShellCryptoCommand(string.Empty, string.Empty);
        if (args.Count != 2) return false;
        var action = args[0];
        if (!action.Equals(EncryptAction, StringComparison.OrdinalIgnoreCase)
            && !action.Equals(DecryptAction, StringComparison.OrdinalIgnoreCase)
            && !action.Equals(RequestAccessAction, StringComparison.OrdinalIgnoreCase))
            return false;
        command = new ShellCryptoCommand(action, args[1]);
        return true;
    }

    public static async Task<int> RunAsync(ShellCryptoCommand command)
    {
        if (command.Action.Equals(RequestAccessAction, StringComparison.OrdinalIgnoreCase))
            return await RunRequestAccessAsync(command.FilePath);

        var pipeClient = new PipeClient();
        var isEncrypt = command.Action.Equals(EncryptAction, StringComparison.OrdinalIgnoreCase);
        try
        {
            var response = await pipeClient.SendAsync(
                DlpMessageTypes.ProtectFile,
                new FileProtectionRequest
                {
                    Action = isEncrypt ? "encrypt" : "decrypt",
                    FilePath = command.FilePath
                },
                timeoutMilliseconds: 120000);

            if (!response.Success) throw new InvalidOperationException(response.Message);
            var result = response.Data?.Deserialize<FileProtectionResponse>(JsonDefaults.Options)
                ?? throw new InvalidOperationException("Invalid response from Al-Ameen service.");
            if (!result.Success) throw new InvalidOperationException(result.Message);

            var message = isEncrypt
                ? $"Encryption and verification completed.\n\nCreated: {Path.GetFileName(result.OutputPath)}\nThe original plaintext file was deleted according to policy."
                : $"Decryption completed.\n\nCreated: {Path.GetFileName(result.OutputPath)}\nThe encrypted .dlpenc file was kept.";
            WpfMessageBox.Show(message, "Al-Ameen", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Al-Ameen operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static async Task<int> RunRequestAccessAsync(string filePath)
    {
        var pipeClient = new PipeClient();
        try
        {
            var response = await pipeClient.SendAsync(
                DlpMessageTypes.RequestFileOpenAccess,
                new FileOpenAccessRequest { FilePath = filePath },
                timeoutMilliseconds: 120000);

            // A response with Success=false but real Data (PermissionDenied) is a legitimate, expected
            // outcome, not a pipe-transport failure - only throw when there's genuinely nothing to
            // work with (Data missing entirely).
            var result = response.Data?.Deserialize<FileOpenAccessResponse>(JsonDefaults.Options)
                ?? throw new InvalidOperationException(response.Message);

            if (result.Success)
            {
                // Launch the now-decrypted file with whatever application Windows has associated with
                // its restored (original) extension - the same as double-clicking it directly would.
                Process.Start(new ProcessStartInfo(result.OutputPath) { UseShellExecute = true });
                return 0;
            }

            if (result.ErrorCode == "PermissionDenied")
            {
                await ShowPermissionDeniedPromptAsync(pipeClient, result);
                return 0;
            }

            WpfMessageBox.Show(result.Message, "Al-Ameen", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Al-Ameen operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    // Mirrors MainWindow.xaml.cs's RequestFileWatermarkDisable_Click deep-link convention exactly
    // (same query parameters, same portal, same "open the default browser" mechanism - no direct API
    // call from the Desktop app itself).
    private static async Task ShowPermissionDeniedPromptAsync(PipeClient pipeClient, FileOpenAccessResponse result)
    {
        var classificationText = string.IsNullOrWhiteSpace(result.Classification) ? "" : $" (classification: {result.Classification})";
        var choice = WpfMessageBox.Show(
            $"You don't have permission to open \"{result.FileName}\"{classificationText} yet - it was received from an external source.\n\nRequest access from your administrator?",
            "Al-Ameen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;

        string? portalBaseUrl = null;
        try
        {
            var policyResponse = await pipeClient.SendAsync(DlpMessageTypes.GetPolicy);
            portalBaseUrl = policyResponse.Data?.Deserialize<DlpPolicy>(JsonDefaults.Options)?.FileClassification.PortalBaseUrl;
        }
        catch { /* Falls through to the "not configured" message below. */ }

        if (string.IsNullOrWhiteSpace(portalBaseUrl))
        {
            WpfMessageBox.Show("The admin portal URL is not configured in this policy.", "Al-Ameen", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var url = $"{portalBaseUrl.TrimEnd('/')}/permission-requests/new" +
            $"?actionKey={Uri.EscapeDataString(ActionKeys.FileOpenAccess)}&fromEvent={result.CorrelationId:D}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show($"Could not open the admin portal: {exception.Message}", "Al-Ameen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
