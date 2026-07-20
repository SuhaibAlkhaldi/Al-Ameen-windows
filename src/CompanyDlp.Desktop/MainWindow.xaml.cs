using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using CompanyDlp.Contracts;
using CompanyDlp.Desktop.Crypto;
using CompanyDlp.Desktop.Development;
using CompanyDlp.Desktop.Notifications;
using CompanyDlp.Desktop.Protection;
using CompanyDlp.Desktop.Services;
using CompanyDlp.Desktop.Watermark;

namespace CompanyDlp.Desktop;

public partial class MainWindow : Window
{
    private readonly PipeClient _pipeClient = new();
    private readonly DevelopmentSessionManager _developmentSession = new();
    private readonly UserAlertService _alertService = new();
    private DlpPolicy? _policy;
    private ServiceStatus? _serviceStatus;
    private ClipboardProtectionManager? _clipboardProtection;
    private ScreenCaptureHotkeyBlocker? _hotkeyBlocker;
    private ScreenRecordingProcessBlocker? _screenRecordingBlocker;
    private WatermarkManager? _watermarkManager;
    private bool _localProtectionStarted;
    private bool _testSessionStarted;
    private DispatcherTimer? _notificationPollTimer;
    private DispatcherTimer? _policyPollTimer;
    private bool _notificationPollInProgress;
    private bool _policyPollInProgress;
    private string _effectivePolicyFingerprint = "";
    private long _lastNotificationId;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_developmentSession.HasUncleanSession)
            {
                _developmentSession.RecoverIfNeeded();
                TestSessionStatusText.Text = "Recovered settings left by an unclean previous test session.";
            }
        }
        catch (Exception exception)
        {
            TestSessionStatusText.Text = $"Recovery warning: {exception.Message}";
        }

        await RefreshStatusWithRetryAsync();
        StartNotificationPolling();
        StartPolicyPolling();
        if (_policy?.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase) == true)
        {
            StartLocalProtection();
            StartTestButton.IsEnabled = false;
            StopTestButton.IsEnabled = false;
            TemporaryUsbBlockButton.IsEnabled = false;
            ResetUsbBaselineButton.IsEnabled = false;
            TestSessionStatusText.Text = "Production mode: protection remains active while the user agent is running.";
        }
    }

    private async Task RefreshStatusWithRetryAsync()
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await RefreshStatusAsync();
            if (_policy is not null) return;
            await Task.Delay(500);
        }
    }

    private async Task RefreshStatusAsync()
    {
        var ping = await _pipeClient.SendAsync(DlpMessageTypes.Ping);
        if (!ping.Success)
        {
            StatusText.Text = "DLP service is not connected";
            StatusIndicator.Fill = WpfBrushes.IndianRed;
            ServiceDetailsText.Text = ping.Message;
            return;
        }

        _serviceStatus = ping.Data?.Deserialize<ServiceStatus>(JsonDefaults.Options);
        var policyResponse = await _pipeClient.SendAsync(DlpMessageTypes.GetPolicy);
        var effectivePolicy = policyResponse.Data?.Deserialize<DlpPolicy>(JsonDefaults.Options);
        if (effectivePolicy is not null) await ApplyEffectivePolicyAsync(effectivePolicy);

        StatusText.Text = $"Connected • {_serviceStatus?.Mode ?? "Unknown"}";
        StatusIndicator.Fill = (WpfBrush)FindResource("SuccessBrush");
        ServiceDetailsText.Text = $"Policy: {_serviceStatus?.PolicyPath} • USB: {_serviceStatus?.EffectiveUsbMode} • Backend: {_serviceStatus?.BackendMode} • Pending events: {_serviceStatus?.PendingAuditEventCount ?? 0}";
    }

    private void StartLocalProtection()
    {
        if (_localProtectionStarted || _policy is null || !_policy.Enabled) return;

        if (_policy.Watermark.Enabled)
        {
            _watermarkManager = new WatermarkManager(_policy.Watermark);
            _watermarkManager.Start();
            WatermarkStatusText.Text = "Active on all detected monitors";
        }

        if (_policy.Clipboard.Enabled)
        {
            _clipboardProtection = new ClipboardProtectionManager(
                this,
                _pipeClient,
                _policy.Clipboard,
                message => Dispatcher.InvokeAsync(() => ClipboardStatusText.Text = message),
                (title, message) => ShowSecurityAlert(title, message));
            _clipboardProtection.Start();
            ClipboardStatusText.Text = "Monitoring clipboard text and fragment sequences";
        }

        if (_policy.Screen.Enabled)
        {
            if (_policy.Screen.ProtectDlpWindowFromCapture) WindowCaptureProtection.ExcludeFromCapture(this);
            _hotkeyBlocker = new ScreenCaptureHotkeyBlocker(
                _policy.Screen,
                _pipeClient,
                message => Dispatcher.InvokeAsync(() => ScreenStatusText.Text = message),
                (title, message) => ShowSecurityAlert(title, message));
            _hotkeyBlocker.Start();

            _screenRecordingBlocker = new ScreenRecordingProcessBlocker(
                _policy.Screen,
                _pipeClient,
                message => Dispatcher.InvokeAsync(() => ScreenStatusText.Text = message),
                (title, message) => Dispatcher.InvokeAsync(() => ShowSecurityAlert(title, message)));
            _screenRecordingBlocker.Start();

            ScreenStatusText.Text = "Screenshot shortcuts and known screen-recording applications are monitored and blocked.";
        }

        _localProtectionStarted = true;
    }

    private void StopLocalProtection()
    {
        _clipboardProtection?.Dispose();
        _clipboardProtection = null;
        _hotkeyBlocker?.Dispose();
        _hotkeyBlocker = null;
        _screenRecordingBlocker?.Dispose();
        _screenRecordingBlocker = null;
        _watermarkManager?.Dispose();
        _watermarkManager = null;
        _localProtectionStarted = false;
        ClipboardStatusText.Text = "Stopped";
        ScreenStatusText.Text = "Stopped";
        WatermarkStatusText.Text = "Stopped";
    }

    private async void StartTest_Click(object sender, RoutedEventArgs e)
    {
        if (_policy is null) await RefreshStatusAsync();
        if (_policy is null) return;
        try
        {
            _developmentSession.Start(_policy);
            StartLocalProtection();
            _testSessionStarted = true;
            StartTestButton.IsEnabled = false;
            StopTestButton.IsEnabled = true;
            TestSessionStatusText.Text = "Test session active. Browser policies are temporary and will be restored on Stop.";
            try
            {
                BrowserStatusText.Text = _developmentSession.LaunchPreferredProtectedBrowser();
                ShowSecurityAlert(
                    "Protected browser opened",
                    "Use a browser profile where Company DLP extension v3.0.0 is enabled. Chrome/Edge can use browser-extension; Firefox uses firefox-extension. Reload pages opened before enabling the extension.",
                    "Info");
            }
            catch (Exception browserException)
            {
                BrowserStatusText.Text = $"Test session started, but protected browser could not be opened: {browserException.Message}";
            }
        }
        catch (Exception exception)
        {
            TestSessionStatusText.Text = $"Could not start test session: {exception.Message}";
        }
    }

    private void StopTest_Click(object sender, RoutedEventArgs e) => StopAndRestore();
    private void EmergencyRestore_Click(object sender, RoutedEventArgs e) => StopAndRestore();
    private void TestAlert_Click(object sender, RoutedEventArgs e) =>
        ShowSecurityAlert(
            "Company DLP test alert",
            "User alerts are enabled. Blocked actions will display the reason in this area.",
            "Info");

    private void StopAndRestore()
    {
        StopLocalProtection();
        try
        {
            _developmentSession.Restore();
            _testSessionStarted = false;
            StartTestButton.IsEnabled = true;
            StopTestButton.IsEnabled = false;
            TestSessionStatusText.Text = "Stopped. Previous browser policy values and normal browser behavior were restored.";
        }
        catch (Exception exception)
        {
            TestSessionStatusText.Text = $"Restore failed: {exception.Message}. Run scripts\\restore-development.ps1 as the current user.";
        }
    }

    private void LaunchEdge_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = _developmentSession.LaunchProtectedEdge();
            BrowserStatusText.Text = $"Protected Edge launched with temporary profile: {profile}";
        }
        catch (Exception exception) { BrowserStatusText.Text = exception.Message; }
    }

    private void LaunchChrome_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = _developmentSession.LaunchProtectedChrome();
            BrowserStatusText.Text = $"Protected Chrome launched with temporary profile: {profile}";
        }
        catch (Exception exception) { BrowserStatusText.Text = exception.Message; }
    }

    private async void TemporaryUsbBlock_Click(object sender, RoutedEventArgs e)
    {
        var response = await _pipeClient.SendAsync(DlpMessageTypes.SetTemporaryUsbBlock, new TemporaryUsbBlockRequest { Minutes = 5 });
        UsbStatusText.Text = response.Success ? response.Message : $"Failed: {response.Message}";
        await RefreshStatusAsync();
    }

    private async void ResetUsbBaseline_Click(object sender, RoutedEventArgs e)
    {
        var response = await _pipeClient.SendAsync(DlpMessageTypes.ResetUsbBaseline);
        UsbStatusText.Text = response.Success ? response.Message : $"Failed: {response.Message}";
    }

    private async void RefreshUsb_Click(object sender, RoutedEventArgs e)
    {
        var response = await _pipeClient.SendAsync(DlpMessageTypes.GetUsbSnapshot);
        var devices = response.Data?.Deserialize<List<UsbDeviceBundleInfo>>(JsonDefaults.Options) ?? [];
        UsbStatusText.Text = devices.Count == 0
            ? "No USB bundles were returned yet. Wait for the service scan."
            : string.Join(Environment.NewLine, devices.Select(item => $"{(item.IsAllowed ? "ALLOW" : "BLOCK")}: {item.DisplayName} [{string.Join(',', item.Classes)}]"));
    }

    private async void ReloadPolicy_Click(object sender, RoutedEventArgs e)
    {
        var response = await _pipeClient.SendAsync(DlpMessageTypes.ReloadPolicy);
        BrowserStatusText.Text = response.Success ? "Policy JSON reloaded." : response.Message;
        await RefreshStatusAsync();
    }

    private async void EncryptFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a file to encrypt",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunCryptoOperationAsync(
            "Encrypting and verifying file…",
            () => ProtectFileThroughServiceAsync("encrypt", dialog.FileName),
            "File encrypted",
            outputPath => $"Created {Path.GetFileName(outputPath)}. The original plaintext file was deleted after integrity verification.");
    }

    private async void DecryptFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a Company DLP encrypted file",
            Filter = "Company DLP encrypted files (*.dlpenc)|*.dlpenc|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunCryptoOperationAsync(
            "Decrypting file…",
            () => ProtectFileThroughServiceAsync("decrypt", dialog.FileName),
            "File decrypted",
            outputPath => $"Created {Path.GetFileName(outputPath)}. The encrypted .dlpenc file was kept.");
    }

    private async Task<string> ProtectFileThroughServiceAsync(string action, string filePath)
    {
        var response = await _pipeClient.SendAsync(
            DlpMessageTypes.ProtectFile,
            new FileProtectionRequest { Action = action, FilePath = filePath },
            timeoutMilliseconds: 120000);
        var result = response.Data?.Deserialize<FileProtectionResponse>(JsonDefaults.Options);
        if (!response.Success)
            throw new InvalidOperationException(result?.Message ?? response.Message);

        if (result is null)
            throw new InvalidOperationException("The Company DLP service returned an invalid file-protection response.");
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return result.OutputPath;
    }

    private async Task RunCryptoOperationAsync(
        string workingText,
        Func<Task<string>> operation,
        string successTitle,
        Func<string, string> successMessage)
    {
        EncryptFileButton.IsEnabled = false;
        DecryptFileButton.IsEnabled = false;
        CryptoStatusText.Text = workingText;

        try
        {
            var outputPath = await operation();
            CryptoStatusText.Text = $"Completed: {outputPath}";
            ShowSecurityAlert(successTitle, successMessage(outputPath), "Info");
            // The Windows Service owns authorization, encryption, and authoritative audit events.
            // Do not create a second UI-only event here.
        }
        catch (Exception exception)
        {
            CryptoStatusText.Text = $"Failed: {exception.Message}";
            ShowSecurityAlert("File encryption operation failed", exception.Message);
            // The Windows Service already records the failed transaction with the same correlation id.
        }
        finally
        {
            EncryptFileButton.IsEnabled = true;
            DecryptFileButton.IsEnabled = true;
        }
    }

    private void StartPolicyPolling()
    {
        if (_policyPollTimer is not null) return;
        _policyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _policyPollTimer.Tick += PolicyPollTimer_Tick;
        _policyPollTimer.Start();
    }

    private async void PolicyPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_policyPollInProgress) return;
        _policyPollInProgress = true;
        try
        {
            var response = await _pipeClient.SendAsync(DlpMessageTypes.GetPolicy);
            var effectivePolicy = response.Data?.Deserialize<DlpPolicy>(JsonDefaults.Options);
            if (effectivePolicy is not null) await ApplyEffectivePolicyAsync(effectivePolicy);
        }
        finally
        {
            _policyPollInProgress = false;
        }
    }

    private async Task ApplyEffectivePolicyAsync(DlpPolicy effectivePolicy)
    {
        var fingerprint = JsonSerializer.Serialize(effectivePolicy, JsonDefaults.Options);
        if (fingerprint.Equals(_effectivePolicyFingerprint, StringComparison.Ordinal))
        {
            _policy = effectivePolicy;
            return;
        }

        var protectionWasRunning = _localProtectionStarted;
        _effectivePolicyFingerprint = fingerprint;

        if (protectionWasRunning) StopLocalProtection();
        if (_testSessionStarted)
        {
            try
            {
                _developmentSession.Restore();
                _developmentSession.Start(effectivePolicy);
            }
            catch (Exception exception)
            {
                TestSessionStatusText.Text = $"Policy refresh warning: {exception.Message}";
            }
        }

        _policy = effectivePolicy;
        if (protectionWasRunning) StartLocalProtection();
        await Task.CompletedTask;
    }

    private void StartNotificationPolling()
    {
        if (_notificationPollTimer is not null) return;

        _notificationPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _notificationPollTimer.Tick += NotificationPollTimer_Tick;
        _notificationPollTimer.Start();
    }

    private async void NotificationPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_notificationPollInProgress || _policy?.Notifications.Enabled == false) return;

        _notificationPollInProgress = true;
        try
        {
            var response = await _pipeClient.SendAsync(
                DlpMessageTypes.GetUserNotifications,
                new NotificationPollRequest { AfterId = _lastNotificationId });
            if (!response.Success) return;

            var notifications = response.Data?.Deserialize<List<UserNotification>>(JsonDefaults.Options) ?? [];
            foreach (var notification in notifications.OrderBy(item => item.Id))
            {
                _lastNotificationId = Math.Max(_lastNotificationId, notification.Id);
                _alertService.Show(notification, GetAlertDurationSeconds());
            }
        }
        finally
        {
            _notificationPollInProgress = false;
        }
    }

    private void ShowSecurityAlert(string title, string message, string severity = "Error")
    {
        if (_policy?.Notifications.Enabled == false) return;
        _alertService.Show(title, message, severity, GetAlertDurationSeconds());
    }

    private int GetAlertDurationSeconds() =>
        Math.Clamp(_policy?.Notifications.DurationSeconds ?? 6, 2, 30);

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _notificationPollTimer?.Stop();
        _notificationPollTimer = null;
        _policyPollTimer?.Stop();
        _policyPollTimer = null;
        _alertService.Dispose();
        if (_policy?.Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase) == true && (_testSessionStarted || _developmentSession.HasUncleanSession))
        {
            StopAndRestore();
        }
        else
        {
            StopLocalProtection();
        }
    }
}
