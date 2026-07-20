using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class BrowserAuditNotificationPolicyTests
{
    [Theory]
    [InlineData("silent-background")]
    [InlineData("silent-background:FormData append")]
    [InlineData("  SILENT-BACKGROUND:page preparation")]
    public void SilentBackgroundBrowserBlock_DoesNotCreateUserNotification(string details)
    {
        var audit = new AuditEvent
        {
            EventType = "browser",
            Action = "formdata-file",
            Result = "blocked",
            Details = details
        };

        Assert.False(BrowserAuditNotificationPolicy.ShouldNotify(audit));
    }


    [Theory]
    [InlineData("formdata-file")]
    [InlineData("formdata-files")]
    [InlineData("xhr-file-upload")]
    [InlineData("fetch-file-upload")]
    [InlineData("beacon-file-upload")]
    public void TransportLayerBrowserBlock_DoesNotCreateUserNotification_WhenMarkerIsMissing(string action)
    {
        var audit = new AuditEvent
        {
            EventType = "browser",
            Action = action,
            Result = "blocked",
            Details = "transport contains File data"
        };

        Assert.False(BrowserAuditNotificationPolicy.ShouldNotify(audit));
    }

    [Fact]
    public void RealUserBrowserBlock_CreatesUserNotification()
    {
        var audit = new AuditEvent
        {
            EventType = "browser",
            Action = "file-picker",
            Result = "blocked",
            Details = "user-click"
        };

        Assert.True(BrowserAuditNotificationPolicy.ShouldNotify(audit));
    }

    [Theory]
    [InlineData("allowed", "browser")]
    [InlineData("blocked", "usb")]
    public void NonBlockedOrNonBrowserAudit_DoesNotCreateBrowserNotification(string result, string eventType)
    {
        var audit = new AuditEvent
        {
            EventType = eventType,
            Action = "file-picker",
            Result = result,
            Details = "user-click"
        };

        Assert.False(BrowserAuditNotificationPolicy.ShouldNotify(audit));
    }
}
