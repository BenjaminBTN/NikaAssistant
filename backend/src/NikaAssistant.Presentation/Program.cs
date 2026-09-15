using Microsoft.Extensions.Options;
using NikaAssistant.Application.Abstractions;
using NikaAssistant.Application.Chat;
using NikaAssistant.Application.CreateTask;
using NikaAssistant.Application.DeleteTask;
using NikaAssistant.Application.GetTask;
using NikaAssistant.Application.Monthly;
using NikaAssistant.Application.Yearly;
using NikaAssistant.Application.UpdateTask;
using NikaAssistant.Contracts;
using NikaAssistant.Domain;
using NikaAssistant.Infrastructure.LLM.OpenRouter;
using NikaAssistant.Infrastructure.LocalStorage;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Локальный секретный файл рядом с exe: не коммитится, приоритет выше appsettings.json.
// Сюда можно положить OpenRouter:ApiKey и переопределить Model/FallbackModels.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

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
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.SectionName));
builder.Services.AddHttpClient<OpenRouterClient>()
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
builder.Services.AddScoped<ILlmClient>(sp => sp.GetRequiredService<OpenRouterClient>());
builder.Services.AddSingleton<IOneTimeTaskStorage, MarkdownOneTimeTaskStorage>();
builder.Services.AddSingleton<IMonthlyTaskStorage, MarkdownMonthlyTaskStorage>();
builder.Services.AddSingleton<IYearlyTaskStorage, MarkdownYearlyTaskStorage>();
builder.Services.AddScoped<MonthlyRolloverService>();
builder.Services.AddScoped<YearlyRolloverService>();
builder.Services.AddScoped<AddTaskHandler>();
builder.Services.AddScoped<GetTaskHandler>();
builder.Services.AddScoped<DeleteTaskHandler>();
builder.Services.AddScoped<DeleteMonthlyTaskHandler>();
builder.Services.AddScoped<DeleteYearlyTaskHandler>();
builder.Services.AddScoped<UpdateTaskHandler>();
builder.Services.AddScoped<UpdateMonthlyTaskHandler>();
builder.Services.AddScoped<UpdateYearlyTaskHandler>();
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

app.MapPost("/AddTask", async (AddTaskRequest request, AddTaskHandler handler, IOneTimeTaskStorage storage, IMonthlyTaskStorage monthlyStorage, IYearlyTaskStorage yearlyStorage, MonthlyRolloverService rollover, YearlyRolloverService yearlyRollover) =>
{
    await handler.AddTaskAsync(request);
    var isMonthly = handler.IsMonthly(request);
    var isYearly = handler.IsYearly(request);
    // Создание ежемесячной/ежегодной с датой сегодня/завтра сразу расщепляем в one-time.
    if (isMonthly)
    {
        await rollover.RolloverDueAsync();
    }
    if (isYearly)
    {
        await yearlyRollover.RolloverDueAsync();
    }
    var assignee = isMonthly ? monthlyStorage.ResolveAssignee(request.Assignee)
        : isYearly ? yearlyStorage.ResolveAssignee(request.Assignee)
        : storage.ResolveAssignee(request.Assignee);
    var dueDate = TaskNormalizer.NormalizeDueDate(request.DueDate);
    var tags = isMonthly ? MonthlyTaskRules.EnsureMonthlyTag(request.Tags)
        : isYearly ? YearlyTaskRules.EnsureYearlyTag(request.Tags)
        : (request.Tags ?? new List<string>());
    var created = new OneTimeTask("[ ]", TaskNormalizer.NormalizeTaskTitle(request.Task), assignee, request.Comment ?? "", tags, dueDate);
    return Results.Ok(created);
});

app.MapGet("/GetTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetTasksAll();
    return Results.Ok(tasks);
});

app.MapGet("/GetMonthlyTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetMonthlyAll();
    return Results.Ok(tasks);
});

app.MapGet("/GetYearlyTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetYearlyAll();
    return Results.Ok(tasks);
});

app.MapGet("/Config", () =>
    Results.Ok(new { defaultAssignee = builder.Configuration["Storage:DefaultAssignee"] ?? "" }));

app.MapGet("/LlmConfig", (IOptionsMonitor<OpenRouterOptions> monitor) =>
{
    var snapshot = monitor.CurrentValue;
    var hasApiKey = !string.IsNullOrWhiteSpace(snapshot.ApiKey)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))
        || !string.IsNullOrWhiteSpace(builder.Configuration["OpenRouter:ApiKey"]);
    return Results.Ok(new
    {
        model = snapshot.Model,
        fallbackModels = snapshot.FallbackModels ?? [],
        hasApiKey,
        referer = snapshot.Referer,
        title = snapshot.Title
    });
});

app.MapPost("/DeleteTask", async (DeleteTaskRequest request, DeleteTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/DeleteMonthlyTask", async (DeleteTaskRequest request, DeleteMonthlyTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/DeleteYearlyTask", async (DeleteTaskRequest request, DeleteYearlyTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/ArchiveCompleted", async (IOneTimeTaskStorage storage, IMonthlyTaskStorage monthlyStorage, IYearlyTaskStorage yearlyStorage) =>
{
    var archived = await storage.ArchiveCompletedAsync();
    archived += await monthlyStorage.ArchiveCompletedAsync();
    archived += await yearlyStorage.ArchiveCompletedAsync();
    return Results.Ok(new { archived });
});

app.MapPost("/UpdateTask", async (UpdateTaskRequest request, UpdateTaskHandler handler) =>
{
    await handler.UpdateTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/UpdateMonthlyTask", async (UpdateTaskRequest request, UpdateMonthlyTaskHandler handler, MonthlyRolloverService rollover) =>
{
    await handler.UpdateTaskAsync(request);
    // Ручная смена даты ежемесячной на сегодня/завтра сразу расщепляем в one-time.
    await rollover.RolloverDueAsync();
    return Results.Ok();
});

app.MapPost("/UpdateYearlyTask", async (UpdateTaskRequest request, UpdateYearlyTaskHandler handler, YearlyRolloverService yearlyRollover) =>
{
    await handler.UpdateTaskAsync(request);
    // Ручная смена даты ежегодной на сегодня/завтра сразу расщепляем в one-time.
    await yearlyRollover.RolloverDueAsync();
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
