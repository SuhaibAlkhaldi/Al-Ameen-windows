namespace CompanyDlp.Service;

public sealed class RuntimeOverrideStore
{
    private readonly object _sync = new();
    private DateTimeOffset? _usbBlockUntilUtc;

    public void EnableTemporaryUsbBlock(TimeSpan duration)
    {
        lock (_sync)
        {
            _usbBlockUntilUtc = DateTimeOffset.UtcNow.Add(duration);
        }
    }

    public string GetUsbMode(string configuredMode)
    {
        lock (_sync)
        {
            if (_usbBlockUntilUtc is { } until && until > DateTimeOffset.UtcNow)
            {
                return "Block";
            }

            _usbBlockUntilUtc = null;
            return configuredMode;
        }
    }
}
