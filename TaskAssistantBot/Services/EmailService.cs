using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Configuration;
using TaskAssistantBot.Models;

namespace TaskAssistantBot.Services;

public class EmailService
{
    private readonly DatabaseContext _db;

    private readonly string _botEmail;
    private readonly string _botPassword;
    private readonly string _smtpHost;
    private readonly int _smtpPort;

    public EmailService(DatabaseContext db, IConfiguration config)
    {
        _db = db;

        // Извлекаем настройки и сразу проверяем на null, чтобы не упасть позже
        _botEmail = config["EmailSettings:BotEmail"]
            ?? throw new Exception("EmailSettings:BotEmail не найден в секретах");
        _botPassword = config["EmailSettings:BotPassword"]
            ?? throw new Exception("EmailSettings:BotPassword не найден в секретах");
        _smtpHost = config["EmailSettings:SmtpHost"] ?? "smtp.yandex.ru";

        _smtpPort = int.Parse(config["EmailSettings:SmtpPort"] ?? "465");
    }

    public async Task<(bool success, string error)> SendEmailAsync(string toEmail, string subject, string body)
    {
        // Просто перенаправляем вызов во внутренний метод
        // Используем email как имя, если оно не передано отдельно
        return await SendEmailInternalAsync(toEmail, toEmail, subject, body);
    }

    // Метод отправки новой задачи. Ищет почту в базе и формирует HTML-письмо
    public async Task<(bool success, string targetEmail)> SendTaskToRecipientAsync(TaskDraft draft)
    {
        // Поиск почты по имени сотрудника
        string recipientEmail = _db.GetEmailByName(draft.RecipientName);
        if (string.IsNullOrEmpty(recipientEmail))
            return (false, $"Контакт '{draft.RecipientName}' не найден");

        string subject = $"📍 Задача: {draft.TaskDescription}";
        string body = $@"
            <h2>Здравствуйте, {draft.RecipientName}!</h2>
            <p>Вам назначена новая задача через Telegram.</p>
            <p><b>📝 Описание:</b> {draft.TaskDescription}</p>
            <p><b>📅 Срок:</b> {draft.Deadline}</p>
            <hr>
            <p><small>Это автоматическое сообщение бота-ассистента.</small></p>";

        var result = await SendEmailInternalAsync(recipientEmail, draft.RecipientName, subject, body);
        return (result.success, result.success ? recipientEmail : result.error);
    }

    // Метод напоминания
    public async Task<(bool success, string message)> SendReminderAsync(TaskDraft draft)
    {
        string recipientEmail = _db.GetEmailByName(draft.RecipientName);
        if (string.IsNullOrEmpty(recipientEmail)) return (false, "Email не найден");

        string subject = $"🔔 НАПОМИНАНИЕ: {draft.TaskDescription}";
        string body = $@"
            <h2>Здравствуйте, {draft.RecipientName}!</h2>
            <p>Это повторное уведомление по задаче:</p>
            <p>📌 <i>{draft.TaskDescription}</i></p>
            <p>⏳ <b>Срок:</b> {draft.Deadline}</p>
            <p>Пожалуйста, не забудьте выполнить её вовремя.</p>";

        var result = await SendEmailInternalAsync(recipientEmail, draft.RecipientName, subject, body);
        return (result.success, result.success ? recipientEmail : result.error);
    }

    // Метод отмены задачи
    public async Task<(bool success, string message)> SendCancellationAsync(TaskDraft task)
    {
        string recipientEmail = _db.GetEmailByName(task.RecipientName);
        if (string.IsNullOrEmpty(recipientEmail)) return (false, "Email не найден");

        string subject = "🚫 Отмена задачи";
        string body = $@"
            <h2 style='color: red;'>Задача отменена</h2>
            <p>Уважаемый(ая) {task.RecipientName}, следующая задача была <b>аннулирована</b>:</p>
            <p style='background: #f0f0f0; padding: 10px;'><i>{task.TaskDescription}</i></p>";

        var result = await SendEmailInternalAsync(recipientEmail, task.RecipientName, subject, body);
        return (result.success, result.success ? "Уведомление отправлено" : result.error);
    }

    // Вспомогательный метод, содержит общую логику работы с SMTP-клиентом
    private async Task<(bool success, string error)> SendEmailInternalAsync(string toEmail, string toName, string subject, string htmlBody)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Бот-Ассистент", _botEmail)); 
            message.To.Add(new MailboxAddress(toName, toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(_smtpHost, _smtpPort, SecureSocketOptions.SslOnConnect); 
            await client.AuthenticateAsync(_botEmail, _botPassword); 
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            return (true, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Email Error]: {ex.Message}");
            return (false, ex.Message);
        }
    }
}