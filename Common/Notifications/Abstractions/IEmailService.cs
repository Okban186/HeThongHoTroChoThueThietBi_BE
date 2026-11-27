namespace HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;

public interface IEmailService
{
    Task SendHtmlAsync(
        IEnumerable<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);
}
