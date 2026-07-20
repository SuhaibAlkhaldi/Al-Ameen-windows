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

    public static bool TryParse(IReadOnlyList<string> args, out ShellCryptoCommand command)
    {
        command = new ShellCryptoCommand(string.Empty, string.Empty);
        if (args.Count != 2) return false;
        var action = args[0];
        if (!action.Equals(EncryptAction, StringComparison.OrdinalIgnoreCase)
            && !action.Equals(DecryptAction, StringComparison.OrdinalIgnoreCase))
            return false;
        command = new ShellCryptoCommand(action, args[1]);
        return true;
    }

    public static async Task<int> RunAsync(ShellCryptoCommand command)
    {
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
                ?? throw new InvalidOperationException("Invalid response from Company DLP service.");
            if (!result.Success) throw new InvalidOperationException(result.Message);

            var message = isEncrypt
                ? $"Encryption and verification completed.\n\nCreated: {Path.GetFileName(result.OutputPath)}\nThe original plaintext file was deleted according to policy."
                : $"Decryption completed.\n\nCreated: {Path.GetFileName(result.OutputPath)}\nThe encrypted .dlpenc file was kept.";
            WpfMessageBox.Show(message, "Company DLP", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Company DLP operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
