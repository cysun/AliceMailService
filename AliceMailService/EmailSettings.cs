using Microsoft.Extensions.Options;

namespace AliceMailService;

public class EmailSettings
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 25;
    public bool RequireAuthentication { get; init; }
    public string Username { get; init; } = "cysun@localhost.localdomain";
    public string Password { get; init; } = "abcd";
    public bool MockSend { get; init; }
    public int SendIntervalMilliseconds { get; init; } = 1000;
    // Leave the bindable array unset: configuration binding appends to initialized arrays.
    // An omitted/null value uses defaults, while an explicit empty array disables retries.
    public int[] RetryDelaysSeconds { get; init; }
    public int[] GetRetryDelaysSeconds() => RetryDelaysSeconds ?? [5, 10, 30];
}

public class EmailSettingsValidator : IValidateOptions<EmailSettings>
{
    public ValidateOptionsResult Validate(string name, EmailSettings settings)
    {
        var errors = new List<string>();
        if (settings.SendIntervalMilliseconds <= 0)
            errors.Add("Email:SendIntervalMilliseconds must be greater than zero.");
        if (settings.GetRetryDelaysSeconds().Any(delay => delay is <= 0 or > 3600))
            errors.Add("Email:RetryDelaysSeconds must contain delays between 1 and 3600 seconds, or be empty to disable retries.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
