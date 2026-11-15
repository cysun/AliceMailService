using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AliceMailService;

// A singleton used by the sequential consumer, so pacing also covers batch boundaries.
public class EmailSender(
    IOptions<EmailSettings> settings,
    Func<ISmtpClient> createClient,
    TimeProvider timeProvider,
    ILogger<EmailSender> logger)
{
    private readonly EmailSettings _settings = settings.Value;
    private readonly int[] _retryDelaysSeconds = settings.Value.GetRetryDelaysSeconds();
    private long? _lastAttemptTimestamp;

    public async Task<List<byte[]>> SendAsync(IReadOnlyList<byte[]> messages, CancellationToken cancellationToken)
    {
        var failed = new List<byte[]>();
        using var client = _settings.MockSend ? null : createClient();

        foreach (var bytes in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MimeMessage message;
            try
            {
                using var stream = new MemoryStream(bytes);
                message = await MimeMessage.LoadAsync(stream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError("Unable to read email; retaining original bytes. Failure type: {failureType}",
                    ex.GetType().Name);
                failed.Add(bytes);
                continue;
            }

            using (message)
            {
                if (!await SendMessageAsync(client, message, cancellationToken))
                    failed.Add(bytes);
            }
        }

        if (client?.IsConnected == true && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failure to QUIT does not undo messages already accepted by SMTP.
                logger.LogWarning("SMTP disconnect failed. Failure type: {failureType}", ex.GetType().Name);
            }
        }

        return failed;
    }

    private async Task<bool> SendMessageAsync(ISmtpClient client, MimeMessage message,
        CancellationToken cancellationToken)
    {
        var recipientCount = message.To.Mailboxes.Count() + message.Cc.Mailboxes.Count() +
                             message.Bcc.Mailboxes.Count();

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_settings.MockSend)
                {
                    if (!client.IsConnected)
                        await client.ConnectAsync(_settings.Host, _settings.Port,
                            _settings.RequireAuthentication ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                            cancellationToken);
                    if (_settings.RequireAuthentication && !client.IsAuthenticated)
                        await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);
                }

                await WaitForSendIntervalAsync(cancellationToken);
                _lastAttemptTimestamp = timeProvider.GetTimestamp();

                if (_settings.MockSend)
                {
                    logger.LogInformation(
                        "Mock email:\nFrom: {from}\nTo: {to}\nCc: {cc}\nBcc: {bcc}\nReply-To: {replyTo}\n" +
                        "Subject: {subject}\nMessage-ID: {messageId}\nDate: {date}\nAttachments: {attachments}\n" +
                        "Text body:\n{textBody}\nHTML body:\n{htmlBody}",
                        message.From.ToString(), message.To.ToString(), message.Cc.ToString(),
                        message.Bcc.ToString(), message.ReplyTo.ToString(), message.Subject, message.MessageId,
                        message.Date, string.Join(", ", message.Attachments.Select(attachment =>
                            attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name ?? "(unnamed)")),
                        message.TextBody ?? string.Empty, message.HtmlBody ?? string.Empty);
                }
                else
                    await client.SendAsync(new SendRequest(message), cancellationToken);

                logger.LogInformation(
                    "Message [{subject}] ({messageId}) {sendResult}; recipients: {recipientCount}; attempt: {attempt}",
                    message.Subject, message.MessageId, _settings.MockSend ? "mock sent" : "accepted by SMTP",
                    recipientCount, attempt + 1);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SmtpCommandException ex) when ((int)ex.StatusCode is >= 400 and < 500 &&
                                                 attempt < _retryDelaysSeconds.Length)
            {
                var delay = TimeSpan.FromSeconds(_retryDelaysSeconds[attempt] *
                                                 (1 + Random.Shared.NextDouble() * 0.2));
                // SMTP exception text can contain a rejected BCC address. Log status and counts only.
                logger.LogWarning(
                    "Message [{subject}] ({messageId}), recipients: {recipientCount}, rejected with SMTP {status}; retry {retry}/{retryCount} in {delaySeconds:F2}s",
                    message.Subject, message.MessageId, recipientCount, (int)ex.StatusCode,
                    attempt + 1, _retryDelaysSeconds.Length, delay.TotalSeconds);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Message [{subject}] ({messageId}), recipients: {recipientCount}, failed after {attempts} attempt(s); SMTP status: {status}; failure type: {failureType}. Retaining for recovery",
                    message.Subject, message.MessageId, recipientCount, attempt + 1,
                    ex is SmtpCommandException smtpException ? (int)smtpException.StatusCode : (int?)null,
                    ex.GetType().Name);
                return false;
            }
        }
    }

    private async Task WaitForSendIntervalAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(_settings.SendIntervalMilliseconds);
        while (_lastAttemptTimestamp is { } lastAttempt)
        {
            var remaining = interval - timeProvider.GetElapsedTime(lastAttempt);
            if (remaining <= TimeSpan.Zero)
                return;
            await Task.Delay(remaining, timeProvider, cancellationToken);
        }
    }
}
