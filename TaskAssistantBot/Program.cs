using Microsoft.Extensions.Configuration;
using TaskAssistantBot;
using TaskAssistantBot.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;

// Инициализация конфигурации из secrets
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

// Параметры бота и администаратора
string token = config["BotSettings:Token"]
    ?? throw new Exception("Telegram Token не найден в secrets.json");
string adminPwd = config["BotSettings:AdminPassword"]
    ?? throw new InvalidOperationException("Критическая ошибка: 'BotSettings:AdminPassword' не настроен в secrets.json. Запуск бота невозможен.");

// Извлечение ключа на нейросети
string groqApiKey = config["ExternalServices:GroqApiKey"]
    ?? throw new Exception("Groq API Key не найден в secrets.json");




// создает контекст и запуск бд, если нет
var db = new DatabaseContext();
db.Initialize();


var botClient = new TelegramBotClient(token);


// Инициализация сервисов 
var llmService = new LlmService(groqApiKey);
var audioService = new AudioService(botClient, groqApiKey);
var emailService = new EmailService(db, config);
var reminderService = new ReminderService(db, emailService);
// Cвязывает все сервисы и обрабатывает логику команд
var engine = new BotEngine(botClient, db, llmService, audioService, emailService, adminPwd);

Console.WriteLine("Бот запущен");
Console.WriteLine("Нажмите Enter для завершения работы...");

using var cts = new CancellationTokenSource();
_ = reminderService.StartAsync(cts.Token);


// Настройка приема обновлений
var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
};

botClient.StartReceiving(
    updateHandler: async (client, update, ct) =>
    {
        try
        {
            await engine.HandleUpdateAsync(update, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при обработке апдейта: {ex.Message}");
        }
    },
    errorHandler: async (client, ex, ct) =>
    {
        Console.WriteLine($"Ошибка Telegram API: {ex.Message}");
    },
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

Console.ReadLine();

cts.Cancel();
Console.WriteLine("Бот остановлен.");