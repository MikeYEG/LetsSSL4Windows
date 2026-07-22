using LetsSSL.Core.Models;
using LetsSSL.Core.Notifications;
using Xunit;

namespace LetsSSL.Core.Tests;

public class NotificationServiceTests
{
    [Fact]
    public async Task SendTest_returns_no_results_when_no_channels_configured()
    {
        var settings = new NotificationSettings();
        var results = await NotificationService.SendTestAsync(settings);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SendTest_skips_email_when_required_fields_missing()
    {
        // Email enabled but no host/from/to -> not a deliverable channel, so no attempt.
        var settings = new NotificationSettings { EmailEnabled = true };
        var results = await NotificationService.SendTestAsync(settings);
        Assert.Empty(results);
    }
}
