using Microsoft.Extensions.Hosting;
using TaskAssistantBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace TaskAssistantBot;

public class ReminderService : BackgroundService
{
    private readonly DatabaseContext _db;
    private readonly EmailService _emailService;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(10)); // проверка раз в 10 сек
  // private readonly PeriodicTimer _timer = new(TimeSpan.FromHours(1)); // Проверка раз в час

    public ReminderService(DatabaseContext db, EmailService emailService)
    {
        _db = db;
        _emailService = emailService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAndSendReminders();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Reminder Error]: {ex.Message}");
            }
        }
    }

    private async Task CheckAndSendReminders()
    {
        var tasks = _db.GetTasksPendingReminder();
        Console.WriteLine($"[ReminderService] Проверка... Найдено задач: {tasks.Count}");

        foreach (var task in tasks)
        {
            // 1. Пытаемся найти email в базе контактов по имени (например, "Антон")
            string targetEmail = _db.GetEmailByName(task.Recipient);

            // 2. Если email не найден, пишем ошибку и пропускаем задачу
            if (string.IsNullOrEmpty(targetEmail))
            {
                Console.WriteLine($"[Reminder Error]: Не найден адрес для контакта '{task.Recipient}'. Проверь таблицу Contacts!");
                continue;
            }

            string subject = "";
            string nextStatus = "";
            var now = DateTime.Now;

            // Логика выбора типа уведомления (оставляем твою)
            if (task.DueDate > now && task.DueDate <= now.AddDays(1))
            {
                subject = "⏳ Напоминание: Срок задачи истекает завтра";
                nextStatus = "ReminderDayBefore";
            }
            else if (now >= task.DueDate && now < task.DueDate.AddDays(1))
            {
                subject = "🔔 Срочно: Сегодня крайний срок задачи!";
                nextStatus = "ReminderToday";
            }
            else if (now >= task.DueDate.AddDays(1))
            {
                subject = "⚠️ Внимание: Задача просрочена!";
                nextStatus = "ReminderAfter";
            }

            if (!string.IsNullOrEmpty(nextStatus))
            {
                // 3. Отправляем письмо уже на НАЙДЕННЫЙ email (targetEmail), а не на имя
                Console.WriteLine($"[ReminderService] Отправка {nextStatus} для {task.Recipient} на адрес {targetEmail}...");

                var result = await _emailService.SendEmailAsync(targetEmail, subject, task.Text);

                if (result.success)
                {
                    _db.UpdateTaskStatus(task.Id, nextStatus);
                    Console.WriteLine($"[ReminderService] Успешно отправлено.");
                }
                else
                {
                    // Используем правильное имя поля из кортежа — error
                    Console.WriteLine($"[Email Error]: {result.error}");
                }
            }
        }
    }
}