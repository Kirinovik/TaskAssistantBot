using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaskAssistantBot.Models;
using TaskAssistantBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TaskAssistantBot
{
    public class BotEngine
    {

        private readonly ITelegramBotClient _botClient;
        private readonly DatabaseContext _db;
        private readonly LlmService _llmService;
        private readonly EmailService _emailService;
        private readonly AudioService _audioService;
        private readonly string _correctPassword;

        public BotEngine(ITelegramBotClient botClient, DatabaseContext db, LlmService llmService, AudioService audioService, EmailService emailService, string correctPassword)
        {
            _botClient = botClient;
            _db = db;
            _llmService = llmService;
            _audioService = audioService;
            _emailService = emailService;
            _correctPassword = correctPassword;
        }

        public async Task HandleUpdateAsync(Update update, CancellationToken ct)
        {
            // Обработка нажатий на кнопки
            if (update.CallbackQuery is { } callback)
            {
                await HandleCallbackAsync(callback, ct);
                return;
            }

            if (update.Message is not { } message) return;
            long chatId = message.Chat.Id;
            string messageText = string.Empty;

            // Получение текста (из голоса или сообщения)
            if (message.Type == MessageType.Text) messageText = message.Text ?? "";
            else if (message.Type == MessageType.Voice)
            {
                await _botClient.SendMessage(chatId, "🎤 Слушаю...", cancellationToken: ct);
                // Вермено скачивает файл
                string path = await _audioService.DownloadVoiceAsync(message.Voice.FileId, ct);
                // Отправка голосового для перевода в текст
                messageText = await _audioService.TranscribeAudioAsync(path);
                await _botClient.SendMessage(chatId, $"Распознано: _{messageText}_", ParseMode.Markdown, cancellationToken: ct);
            }
            else return;

            
            if (messageText.StartsWith("/start"))
            {
                bool isAuthenticated = _db.IsUserAuthenticated(chatId);
                string statusEmoji = isAuthenticated ? "✅ Авторизован" : "❌ Не авторизован";

                string welcomeMessage =
                    "👋 Добро пожаловать в Task Assistant Bot!\n\n" +
                    "Этот бот предназначен для управления рабочими задачами:\n" +
                    "🎤 Голосовой ввод: Просто надиктуйте задачу.\n" +
                    "📧 Email-уведомления: Бот сам найдет контакт и отправит письмо.\n" +
                    "📋 Управление: Просмотр списка и отмена задач по ID.\n\n" +
                    $"Ваш статус: {statusEmoji}\n\n";

                if (!isAuthenticated)
                {
                    welcomeMessage += "🔐 Чтобы начать работу, пожалуйста, введите пароль.";
                }
                else
                {
                    welcomeMessage += "🚀 Вы можете отправлять задачи или для просмтора всех команд используйте `/help`";
                }

                await _botClient.SendMessage(chatId, welcomeMessage, ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            // Проверка авторизации
            if (!_db.IsUserAuthenticated(chatId))
            {
                if (messageText == _correctPassword)
                {
                    _db.AuthenticateUser(chatId);
                    await _botClient.SendMessage(chatId, "✅ Доступ разрешен! Теперь вы можете ставить задачи.");
                }
                else
                {
               
                    string errorPrefix = (messageText == "/start")
                        ? "🔐 Для работы с ботом, пожалуйста, введите пароль:"
                        : "❌ Неверный пароль. Попробуйте еще раз:";

                    await _botClient.SendMessage(chatId, errorPrefix, ParseMode.Markdown);
                }
                return;
            }

            // Обработка команд
            if (messageText.StartsWith("/list"))
            {
                var tasks = _db.GetActiveTasks(); 
                if (!tasks.Any())
                {
                    await _botClient.SendMessage(chatId, "📭 Список активных задач пуст.");
                    return;
                }

                string report = "📋 **Активные задачи:**\n\n";
                foreach (var t in tasks)
                {
                    report += $"ID `{t.Id}` | **{t.Recipient}**: {t.Text}\n";
                }

                await _botClient.SendMessage(chatId, report, ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            if (messageText.StartsWith("/add"))
            {
                await HandleAddCommand(chatId, messageText);
                return;
            }

            if (messageText.StartsWith("/remind"))
            {
                await HandleRemindCommand(chatId, messageText);
                return;
            }

            if (messageText.StartsWith("/cancel"))
            {
                var parts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out int taskId))
                {
                    await _botClient.SendMessage(chatId, "⚠️ Укажите ID задачи. Пример: `/cancel 15`", ParseMode.Markdown);
                    return;
                }

                //  Получаем данные задачи из базы перед тем, как пометить её отмененной
                var taskToCancel = _db.GetTaskById(taskId);
                if (taskToCancel == null)
                {
                    await _botClient.SendMessage(chatId, $"❌ Задача с ID `{taskId}` не найдена.");
                    return;
                }

                // 2. Уведомляем пользователя по Email
                await _botClient.SendMessage(chatId, $"⏳ Уведомляю {taskToCancel.RecipientName} об отмене задачи №{taskId}...");
                var emailResult = await _emailService.SendCancellationAsync(taskToCancel);

                if (emailResult.success)
                {
                    // 3. Если письмо ушло, меняем статус в базе на 'Cancelled'
                    // Теперь она перестанет отображаться в /list
                    _db.UpdateTaskStatus(taskId, "Cancelled");
                    await _botClient.SendMessage(chatId, $"✅ Задача №{taskId} отменена и скрыта из списка.");
                }
                else
                {
                    await _botClient.SendMessage(chatId, $"❌ Ошибка при отправке уведомления: {emailResult.message}. Задача не отменена.");
                }
                return;
            }

            if (messageText.StartsWith("/logout"))
            {
                _db.LogoutUser(chatId);
                await _botClient.SendMessage(chatId, "🔒 Вы вышли из учетной записи. Для работы введите пароль снова.");
                return;
            }

            if (messageText.StartsWith("/help"))
            {
                bool isAuthenticated = _db.IsUserAuthenticated(chatId);

                string helpMessage = "🆘 Справка по командам бота\n\n" +
                                     "Общие команды (доступны всегда):\n" +
                                     "`/start` — Перезапустить бота и проверить статус авторизации.\n" +
                                     "`/help` — Показать это справочное сообщение.\n\n";

                if (isAuthenticated)
                {
                    helpMessage += "🔓 Команды для авторизованных пользователей:\n" +
                                   "`🎙 Отправка голоса` — Просто надиктуйте задачу (ИИ сам её разберет).\n" +
                                   "`/list` — Показать последние 25 активных задач и их ID.\n" +
                                   "`/cancel [ID]` — Отменить задачу по её номеру (например: `/cancel 5`).\n" +
                                   "`/contacts` — Показать список всех сотрудников и их Email.\n" +
                                   "`/add [Имя] [Email]` — Добавить новый контакт (например: `/add Иван ivan@mail.ru`).\n" +
                                   "`/remind [Имя]` — Отправить напоминание по последней задаче человека.\n" +
                                   "`/logout` — Выйти из учетной записи.\n\n" +
                                   "💡 Вы также можете просто написать текст задачи боту , и он предложит её сохранить.";
                }
                else
                {
                    helpMessage += "🔒 Функции закрыты:\n" +
                                   "Для доступа к управлению задачами, добавлению контактов и просмотру списка сотрудников, пожалуйста, введите пароль.";
                }
                await _botClient.SendMessage(chatId, helpMessage, ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            if (messageText.StartsWith("/contacts"))
            {
                var contacts = _db.GetAllContacts();

                if (!contacts.Any())
                {
                    await _botClient.SendMessage(chatId, "📭 Список контактов пуст. Добавьте кого-нибудь через `/add`.");
                    return;
                }

                string report = "👥 Список сотрудников:\n\n";
                foreach (var contact in contacts)
                {
                    report += $"• {contact.Name}: `{contact.Email}`\n";
                }

                await _botClient.SendMessage(chatId, report, ParseMode.Markdown, cancellationToken: ct);
                return;
            }

            // Если пользователь авторизирован и это не команда, то отправленый текст анализируется ИИ для постановки задачи
            await ProcessAiTask(chatId, messageText, ct);
        }

        private async Task HandleAddCommand(long chatId, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                string email = parts[^1];
                string name = string.Join(" ", parts[1..^1]);
                _db.AddContact(name, email);
                await _botClient.SendMessage(chatId, $"✅ Контакт сохранен: {name}");
            }
            else await _botClient.SendMessage(chatId, "⚠️ Формат: `/add Имя Фамилия почта@mail.ru`", ParseMode.Markdown);
        }

        private async Task HandleRemindCommand(long chatId, string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { await _botClient.SendMessage(chatId, "⚠️ Укажите имя."); return; }

            var lastTask = _db.GetLastTaskForRecipient(parts[1]);
            if (lastTask == null) { await _botClient.SendMessage(chatId, "❌ Задач не найдено."); return; }

            var result = await _emailService.SendReminderAsync(lastTask);
            if (result.success)
            {
                await _botClient.SendMessage(chatId, "🔔 Напоминание отправлено!");
                _db.LogTask(chatId, lastTask.RecipientName, lastTask.TaskDescription, "Reminder Sent");
            }
            else await _botClient.SendMessage(chatId, $"❌ Ошибка: {result.message}");
        }

        private async Task ProcessAiTask(long chatId, string text, CancellationToken ct)
        {
            await _botClient.SendMessage(chatId, "🤖 Обрабатываю запрос...", cancellationToken: ct);
            _db.ClearDraft(chatId);
            _db.UpdateDraft(chatId, text);

            try
            {
                TaskDraft draft = await _llmService.ParseTaskAsync(text);
                string response = $"🔍 Подтвердите задачу:\n\n👤 Кому: {draft.RecipientName}\n📝 Задача: {draft.TaskDescription}\n📅 Срок: {draft.Deadline}";

                var keyboard = new InlineKeyboardMarkup(new[] {
                    new[] { InlineKeyboardButton.WithCallbackData("✅ Отправить", "send_task"), InlineKeyboardButton.WithCallbackData("❌ Отмена", "cancel_task") }
                });

                await _botClient.SendMessage(chatId, response, ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                // Логируем ошибку в консоль, чтобы соответствовать ТЗ (не пустой catch)
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error @ ProcessAiTask]: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"Inner: {ex.InnerException.Message}");
                Console.ResetColor();

                // Уведомляем пользователя
                await _botClient.SendMessage(chatId, "❌ Не удалось разобрать задачу. Попробуйте ввести данные вручную или уточнить запрос.", cancellationToken: ct);
            }
        }

        private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
        {
            long chatId = callback.Message.Chat.Id;
            if (callback.Data == "send_task")
            {
                string raw = _db.GetDraft(chatId);
                TaskDraft draft = await _llmService.ParseTaskAsync(raw);
                var result = await _emailService.SendTaskToRecipientAsync(draft);
                if (result.success)
                {
                    await _botClient.EditMessageText(chatId, callback.Message.MessageId, $"✅ Отправлено на {result.targetEmail}");
                    _db.SaveFinalTask(chatId, draft);
                    _db.ClearDraft(chatId);
                }
            }
            else if (callback.Data == "cancel_task")
            {
                _db.ClearDraft(chatId);
                await _botClient.EditMessageText(chatId, callback.Message.MessageId, "🗑 Отменено.");
            }
            await _botClient.AnswerCallbackQuery(callback.Id);
        }
    }
}