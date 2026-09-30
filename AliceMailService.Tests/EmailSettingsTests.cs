using System.Text;
using Microsoft.Extensions.Configuration;

namespace AliceMailService.Tests;

public class EmailSettingsTests
{
    [Theory]
    [InlineData("[1, 2]", new[] { 1, 2 })]
    [InlineData("[5, 10, 30]", new[] { 5, 10, 30 })]
    [InlineData("[]", new int[0])]
    public void JsonRetryPolicyReplacesDefaults(string jsonDelays, int[] expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"RetryDelaysSeconds\":" + jsonDelays + "}"));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var settings = new EmailSettings();
        configuration.Bind(settings);
        Assert.Equal(expected, settings.RetryDelaysSeconds);
    }

    [Fact]
    public void DefaultsAreValidAndMatchDocumentedPolicy()
    {
        var settings = new EmailSettings();
        Assert.Equal(1000, settings.SendIntervalMilliseconds);
        Assert.Equal(new[] { 5, 10, 30 }, settings.GetRetryDelaysSeconds());
        Assert.True(new EmailSettingsValidator().Validate(null, settings).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositiveSendInterval(int interval)
    {
        Assert.True(new EmailSettingsValidator().Validate(null,
            new EmailSettings { SendIntervalMilliseconds = interval }).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void RejectsInvalidRetryDelay(int delay)
    {
        Assert.True(new EmailSettingsValidator().Validate(null,
            new EmailSettings { RetryDelaysSeconds = [delay] }).Failed);
    }

    [Fact]
    public void UsesDefaultsForOmittedOrNullRetryPolicyAndAllowsDisablingRetries()
    {
        var validator = new EmailSettingsValidator();
        Assert.True(validator.Validate(null, new EmailSettings { RetryDelaysSeconds = null }).Succeeded);
        Assert.Equal(new[] { 5, 10, 30 }, new EmailSettings { RetryDelaysSeconds = null }.GetRetryDelaysSeconds());
        Assert.True(validator.Validate(null, new EmailSettings { RetryDelaysSeconds = [] }).Succeeded);
        Assert.Empty(new EmailSettings { RetryDelaysSeconds = [] }.GetRetryDelaysSeconds());
    }
}
