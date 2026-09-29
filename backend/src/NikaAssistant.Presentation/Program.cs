using Microsoft.Extensions.Options;
using NikaAssistant.Application.Abstractions;
using NikaAssistant.Application.Chat;
using NikaAssistant.Application.CreateTask;
using NikaAssistant.Application.DeleteTask;
using NikaAssistant.Application.GetTask;
using NikaAssistant.Application.Recurring;
using NikaAssistant.Application.UpdateTask;
using NikaAssistant.Contracts;
using NikaAssistant.Domain;
using NikaAssistant.Infrastructure.LLM;
using NikaAssistant.Infrastructure.LLM.Gemini;
using NikaAssistant.Infrastructure.LLM.OpenAICompatible;
using NikaAssistant.Infrastructure.LocalStorage;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Локальный секретный файл рядом с exe: не коммитится, приоритет выше appsettings.json.
// Сюда кладётся LLM-ключ (для Gemini — из Google AI Studio) и переопределяются
// Provider/Model/FallbackModels/BaseUrl.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Запуск exe (особенно из папки publish) сразу прописывает его в автозапуск:
// Windows — реестр HKCU\Run, Linux — XDG Autostart, macOS — LaunchAgent.
// В запись дописывается --from-autostart, чтобы старт из автозапуска опознавался (без popup).
// Отключение: "Autostart": { "Enabled": false } или флаг --no-autostart.
NikaAssistant.Presentation.Autostart.EnsureRegistered(builder.Configuration, args);

var logDirectory = ResolveLogDirectory(builder.Configuration);
var retainedDays = builder.Configuration.GetValue<int?>("Logging:File:RetainedDays") is { } days and > 0 ? days : 30;
Directory.CreateDirectory(logDirectory);

builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .MinimumLevel.Is(ParseLevel(context.Configuration["Logging:LogLevel:Default"], LogEventLevel.Information))
    .MinimumLevel.Override("Microsoft.AspNetCore", ParseLevel(context.Configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning))
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "nika-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: retainedDays,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

builder.Services.AddOpenApi();
// Унифицированные настройки LLM: новая секция "Llm" имеет высший приоритет,
// "Gemini" — алиас, legacy "OpenRouter" подхватывается для обратной совместимости
// (старые appsettings.Local.json с OpenRouter:ApiKey продолжат работать).
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("OpenRouter"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.AddHttpClient<GeminiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    });
builder.Services.AddHttpClient<OpenAiCompatibleClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(1)
    });
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ILlmClient, LlmClientRouter>();
builder.Services.AddSingleton<IOneTimeTaskStorage, MarkdownOneTimeTaskStorage>();
builder.Services.AddSingleton<IRecurringTaskStorage, MarkdownRecurringTaskStorage>();
builder.Services.AddScoped<RecurringRolloverService>();
builder.Services.AddScoped<AddTaskHandler>();
builder.Services.AddScoped<GetTaskHandler>();
builder.Services.AddScoped<DeleteTaskHandler>();
builder.Services.AddScoped<DeleteRecurringTaskHandler>();
builder.Services.AddScoped<UpdateTaskHandler>();
builder.Services.AddScoped<UpdateRecurringTaskHandler>();
builder.Services.AddScoped<ChatService>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseSession();

if(app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    http.Response.Headers.Pragma = "no-cache";
    var filePath = Path.Combine(app.Environment.ContentRootPath, "Views", "index.html");
    return Results.File(filePath, "text/html");
});

app.MapPost("/AddTask", async (AddTaskRequest request, AddTaskHandler handler, IOneTimeTaskStorage storage, IRecurringTaskStorage recurringStorage, RecurringRolloverService rollover) =>
{
    await handler.AddTaskAsync(request);
    var isRecurring = handler.IsRecurring(request);
    // Создание периодической с датой сегодня/завтра сразу расщепляем в one-time.
    if (isRecurring)
    {
        await rollover.RolloverDueAsync();
    }
    var assignee = isRecurring ? recurringStorage.ResolveAssignee(request.Assignee)
        : storage.ResolveAssignee(request.Assignee);
    var dueDate = TaskNormalizer.NormalizeDueDate(request.DueDate);
    var tags = isRecurring ? RecurringTaskRules.MigrateLegacyTags(request.Tags)
        : (request.Tags ?? new List<string>());
    var created = new OneTimeTask("[ ]", TaskNormalizer.NormalizeTaskTitle(request.Task), assignee, request.Comment ?? "", tags, dueDate);
    return Results.Ok(created);
});

app.MapGet("/GetTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetTasksAll();
    return Results.Ok(tasks);
});

app.MapGet("/GetRecurringTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetRecurringAll();
    return Results.Ok(tasks);
});

// Устаревшие алиасы (до объединения): отдают тот же единый список периодических задач.
app.MapGet("/GetMonthlyTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetRecurringAll();
    return Results.Ok(tasks);
});

app.MapGet("/GetYearlyTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetRecurringAll();
    return Results.Ok(tasks);
});

app.MapGet("/Config", () =>
    Results.Ok(new { defaultAssignee = builder.Configuration["Storage:DefaultAssignee"] ?? "" }));

app.MapGet("/LlmConfig", (IOptionsMonitor<LlmOptions> monitor) =>
{
    var snapshot = monitor.CurrentValue;
    var hasApiKey = !string.IsNullOrWhiteSpace(snapshot.ApiKey)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LLM_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))
        || !string.IsNullOrWhiteSpace(builder.Configuration["Llm:ApiKey"])
        || !string.IsNullOrWhiteSpace(builder.Configuration["Gemini:ApiKey"])
        || !string.IsNullOrWhiteSpace(builder.Configuration["OpenRouter:ApiKey"]);
    return Results.Ok(new
    {
        provider = snapshot.Provider,
        model = snapshot.Model,
        fallbackModels = snapshot.FallbackModels ?? [],
        hasApiKey,
        baseUrl = snapshot.BaseUrl,
        referer = snapshot.Referer,
        title = snapshot.Title
    });
});

app.MapPost("/DeleteTask", async (DeleteTaskRequest request, DeleteTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/DeleteRecurringTask", async (DeleteTaskRequest request, DeleteRecurringTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

// Устаревшие алиасы (до объединения).
app.MapPost("/DeleteMonthlyTask", async (DeleteTaskRequest request, DeleteRecurringTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/DeleteYearlyTask", async (DeleteTaskRequest request, DeleteRecurringTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/ArchiveCompleted", async (IOneTimeTaskStorage storage, IRecurringTaskStorage recurringStorage) =>
{
    var archived = await storage.ArchiveCompletedAsync();
    archived += await recurringStorage.ArchiveCompletedAsync();
    return Results.Ok(new { archived });
});

app.MapPost("/UpdateTask", async (UpdateTaskRequest request, UpdateTaskHandler handler) =>
{
    await handler.UpdateTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/UpdateRecurringTask", async (UpdateTaskRequest request, UpdateRecurringTaskHandler handler, RecurringRolloverService rollover) =>
{
    await handler.UpdateTaskAsync(request);
    // Ручная смена даты периодической на сегодня/завтра сразу расщепляем в one-time.
    await rollover.RolloverDueAsync();
    return Results.Ok();
});

// Устаревшие алиасы (до объединения).
app.MapPost("/UpdateMonthlyTask", async (UpdateTaskRequest request, UpdateRecurringTaskHandler handler, RecurringRolloverService rollover) =>
{
    await handler.UpdateTaskAsync(request);
    // Ручная смена даты периодической на сегодня/завтра сразу расщепляем в one-time.
    await rollover.RolloverDueAsync();
    return Results.Ok();
});

app.MapPost("/UpdateYearlyTask", async (UpdateTaskRequest request, UpdateRecurringTaskHandler handler, RecurringRolloverService rollover) =>
{
    await handler.UpdateTaskAsync(request);
    // Ручная смена даты периодической на сегодня/завтра сразу расщепляем в one-time.
    await rollover.RolloverDueAsync();
    return Results.Ok();
});

app.MapPost("/Chat", async (ChatRequest request, ChatService chatService) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "Сообщение не может быть пустым" });
    }

    var answer = await chatService.AskAsync(request.Message);
    return Results.Ok(new { answer = answer.Answer, addedTasks = answer.AddedTasks });
});

// Всплывающее окно «Ваш ассистент запущен» после старта сервера (в фоне, старт не блокирует).
// Отключение: "StartupPopup": { "Enabled": false } или флаг --no-popup.
app.Lifetime.ApplicationStarted.Register(() =>
    NikaAssistant.Presentation.StartupNotifier.ShowStarted(args, app.Urls, builder.Configuration));

app.Run();

static string ResolveLogDirectory(IConfiguration configuration)
{
    var raw = configuration["Logging:File:Directory"];
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "NikaAssistant", "Logs");
    }

    // Раскрывает %USERPROFILE% (Windows) / $HOME (Unix), чтобы путь работал у любого пользователя.
    raw = Environment.ExpandEnvironmentVariables(raw);
    if (raw.StartsWith("~"))
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        raw = Path.Combine(home, raw.Substring(1).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    return raw;
}

static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
    Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) ? level : fallback;
