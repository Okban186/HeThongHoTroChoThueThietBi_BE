using System.Net;
using System.Net.Mail;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Configurations;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Common.Notifications.Services;

public sealed class SmtpEmailService(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly EmailOptions _opt = options.Value;

    public async Task SendHtmlAsync(
        IEnumerable<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        var distinct = recipients
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.Contains('@'))
            .Select(e => e.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
            return;

        if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.SmtpHost) || string.IsNullOrWhiteSpace(_opt.FromEmail))
        {
            logger.LogInformation(
                "[Email Disabled/Unconfigured] Bỏ qua gửi mail '{Subject}' tới {Recipients}",
                subject,
                string.Join(", ", distinct));
            return;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(_opt.FromEmail, _opt.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };

            foreach (var addr in distinct)
                message.To.Add(addr);

            using var client = new SmtpClient(_opt.SmtpHost, _opt.SmtpPort)
            {
                EnableSsl = _opt.EnableSsl,
                Credentials = new NetworkCredential(_opt.Username, _opt.Password),
                Timeout = 15000,
            };

            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gửi email '{Subject}' thất bại.", subject);
        }
    }
}
