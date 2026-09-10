using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NikaAssistant.Contracts;
using NikaAssistant.Infrastructure.LLM;
using NikaAssistant.Infrastructure.LocalStorage;
using System.Globalization;
using System.Text.Json;

namespace NikaAssistant.Application.Chat;

public sealed class ChatService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private const string SystemPromptTemplate =
        "Ты — помощница по имени Ника. Общайся на русском. Сегодня: {0}."
        + " Относительные даты («сегодня», «завтра», «на этой неделе» и т.п.) отсчитывай строго от этой даты,"
        + " а не из своих знаний о календаре. Дату dueDate всегда вычисляй от сегодняшней даты и указывай в формате yyyy-MM-dd HH:mm."
        + " Инструмент add_task вызывай СТРОГО только когда пользователь явно просит добавить, создать или записать задачу,"
        + " включая короткие follow-up сообщения («ещё такую же», «и вторую», «такую же на завтра», «повтори») — это тоже просьбы добавить."
        + " Во всех остальных случаях (вопросы, болтовня, уточнения) просто отвечай текстом и никаких задач не создавай."
        + " Каждое новое сообщение пользователя с просьбой добавить задачу — это НОВАЯ задача: вызывай add_task даже если текст похож на уже добавленную ранее."
        + " Ранее добавленные задачи не считай поводом пропускать вызов."
        + " Запрет на дубли действует только внутри одного твоего ответа: на одну задачу в одном ответе вызывай add_task РОВНО ОДИН раз."
        + " Название задачи (поле task) всегда начинай с заглавной буквы."
        + " Теги ставь только в самых очевидных случаях: «Срочно» — только если пользователь прямо пишет про срочность"
        + " («срочно», «немедленно», «горит» и т.п.); «Ожидание» и «Зависло» — только при явном указании."
        + " Если уверенности нет — вообще не передавай поле tags.";

    private const string HistoryKey = "chat_history";
    private const int MaxHistoryMessages = 40;

    // Маркеры явной срочности в сообщении пользователя. «Срочно» от модели засчитывается
    // только если в запросе есть один из них, — иначе тег снимается кодом.
    private static readonly string[] UrgencyMarkers =
    [
        "срочн", "немедленн", "неотложн", "asap", "горит", "горят",
        "прямо сейчас", "как можно скорее", "кровь из носу", "сегодня же",
    ];
    // Маркеры явной просьбы добавить задачу. Проверка грубая (подстрока, без учёта регистра):
    // ретрай с принудительным tool_choice срабатывает только если первый ответ модели
    // пришёл вообще без tool calls, так что ложное срабатывание максимум стоит одного лишнего запроса.
    private static readonly string[] TaskIntentMarkers =
    [
        "добав", "созда", "запиш", "поставь задачу", "новая задача", "напомни",
        "еще такую", "ещё такую", "такую же", "и вторую", "внеси", "занеси", "зафиксируй",
    ];

    private const string AddTaskSchemaTemplate =
        """
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "Текст задачи. Первое слово всегда с заглавной буквы" },
            "assignee": { "type": "string", "description": "Ответственный за задачу" },
            "comment": { "type": "string", "description": "Комментарий к задаче" },
            "dueDate": { "type": "string", "description": "Срок исполнения в формате yyyy-MM-dd HH:mm. Сегодня {TODAY}. Если пользователь не указал срок или время — не передавай это поле (по умолчанию будет установлено сегодня 19:00). Если указана только дата без времени — передавай дату с временем 19:00. Если пользователь сказал «сегодня» — передавай {TODAY} 19:00, если «завтра» — завтрашнюю дату 19:00" },
            "tags": { "type": "array", "items": { "type": "string", "enum": ["Срочно", "Зависло", "Ожидание"] }, "description": "Теги задачи. Передавай ТОЛЬКО при явном указании: «Срочно» — если пользователь прямо сказал про срочность, «Ожидание»/«Зависло» — если прямо сказано. Во всех остальных случаях не передавай это поле" }
          },
          "required": ["task"]
        }
        """;

    private static LlmTool BuildAddTaskTool(string todayDate) => new(
        "add_task",
        "Добавить новую разовую задачу в список задач пользователя. Используй, когда пользователь просит создать или добавить задачу.",
        AddTaskSchemaTemplate.Replace("{TODAY}", todayDate));

    private readonly ILlmClient _llm;
    private readonly IOneTimeTaskStorage _storage;
    private readonly ISession? _session;
    private readonly ILogger<ChatService> _logger;

    public ChatService(ILlmClient llm, IOneTimeTaskStorage storage, IHttpContextAccessor httpContextAccessor, ILogger<ChatService> logger)
    {
        _llm = llm;
        _storage = storage;
        _session = httpContextAccessor.HttpContext?.Session;
        _logger = logger;
    }

    public async Task<ChatResult> AskAsync(string message, CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var todayDate = now.ToString("yyyy-MM-dd");
        var todayHuman = now.ToString("d MMMM yyyy (dddd), HH:mm", new CultureInfo("ru-RU"));
        var systemPrompt = string.Format(CultureInfo.InvariantCulture, SystemPromptTemplate, todayHuman);
        var addTaskTool = BuildAddTaskTool(todayDate);

        var messages = new List<LlmMessage> { new("system", systemPrompt) };
        messages.AddRange(LoadHistory());
        messages.Add(new LlmMessage("user", message));

        _logger.LogInformation("Chat message: {Message}", message);

        var response = await _llm.CompleteAsync(messages, new[] { addTaskTool }, cancellationToken);
        if (response.IsError)
        {
            // Сбой провайдера (DEGRADED, rate limit и т.п.): историю не трогаем, чтобы повтор был чистым,
            // а пользователю показываем понятный текст вместо сырого JSON ошибки.
            _logger.LogWarning("Chat provider error: {Detail}", response.Content);
            return new ChatResult(
                "Провайдер модели временно недоступен, попробуйте повторить через минуту.",
                Array.Empty<OneTimeTask>());
        }
        messages.Add(ToAssistantMessage(response));
        _logger.LogInformation("Chat LLM answer by {Model}, tool calls: {ToolCallCount}: {Content}",
            response.Model ?? "unknown", response.ToolCalls.Count, Truncate(response.Content, 1000));

        if (response.ToolCalls.Count == 0 && LooksLikeAddTaskRequest(message))
        {
            // Модель иногда отвечает «Добавил задачу» текстом, не вызывая add_task.
            // Повторяем тот же запрос с принудительным tool_choice — один раз.
            _logger.LogInformation("Chat no tool calls for add-task-like message, retrying with forced tool_choice.");
            try
            {
                var retry = await _llm.CompleteAsync(messages, new[] { addTaskTool }, cancellationToken, "add_task");
                if (retry.IsError)
                {
                    _logger.LogWarning("Chat forced retry provider error: {Detail}", retry.Content);
                }
                else
                {
                    _logger.LogInformation("Chat forced retry answer by {Model}, tool calls: {ToolCallCount}: {Content}",
                        retry.Model ?? "unknown", retry.ToolCalls.Count, Truncate(retry.Content, 1000));
                    // В историю пишем только итоговый обмен, чтобы не плодить мусор из первой попытки.
                    messages.RemoveAt(messages.Count - 1);
                    messages.Add(ToAssistantMessage(retry));
                    response = retry;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chat forced retry failed, using original answer.");
            }
        }

        var addedTasks = new List<OneTimeTask>();
        var seenTaskKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var call in response.ToolCalls)
        {
            if (call.Name == "add_task")
            {
                _logger.LogInformation("Chat add_task call {CallId}: {Args}", call.Id, call.ArgumentsJson);
                var request = JsonSerializer.Deserialize<AddTaskRequest>(call.ArgumentsJson, JsonOptions);
                if (request is null || string.IsNullOrWhiteSpace(request.Task))
                {
                    messages.Add(new LlmMessage("tool", "Не удалось разобрать аргументы задачи.", call.Id));
                    continue;
                }
                request = request with { Task = MarkdownOneTimeTaskStorage.NormalizeTaskTitle(request.Task) };
                var hadUrgentTag = request.Tags?.Any(t => t.Equals("Срочно", StringComparison.OrdinalIgnoreCase)) ?? false;
                request = request with { Tags = StripUnjustifiedUrgentTag(request.Tags, message) };
                var urgentStripped = hadUrgentTag &&
                    !(request.Tags?.Any(t => t.Equals("Срочно", StringComparison.OrdinalIgnoreCase)) ?? false);
                if (urgentStripped)
                {
                    _logger.LogInformation("Chat stripped unjustified 'Срочно' tag for task: {Task}", request.Task);
                }
                if (!seenTaskKeys.Add(request.Task.Trim()))
                {
                    // LLM иногда присылает несколько одинаковых вызовов в одном ответе —
                    // повторный вызов пропускаем, чтобы не плодить дубликаты в файле.
                    _logger.LogWarning("Chat add_task duplicate skipped: {Task}", request.Task.Trim());
                    messages.Add(new LlmMessage("tool", $"Дублирующий вызов для задачи «{request.Task.Trim()}» пропущен: задача уже добавлена.", call.Id));
                    continue;
                }
                await _storage.AddAsync(request, cancellationToken);
                var effectiveAssignee = _storage.ResolveAssignee(request.Assignee);
                addedTasks.Add(new OneTimeTask("[ ]", request.Task, effectiveAssignee, request.Comment ?? "", request.Tags ?? new List<string>(), MarkdownOneTimeTaskStorage.NormalizeDueDate(request.DueDate)));
                var toolNote = urgentStripped
                    ? $"Задача успешно добавлена: {request.Task}. Тег «Срочно» снят: явной срочности в запросе нет. В ответе пользователю не упоминай тег «Срочно» и не утверждай, что он поставлен."
                    : $"Задача успешно добавлена: {request.Task}";
                messages.Add(new LlmMessage("tool", toolNote, call.Id));
            }
        }

        if (response.ToolCalls.Count > 0)
        {
            var final = await _llm.CompleteAsync(messages, null, cancellationToken);
            if (final.IsError)
            {
                _logger.LogWarning("Chat provider error on final answer: {Detail}", final.Content);
                // Задачи к этому моменту уже записаны в файл — историю сохраняем,
                // чтобы повторный запрос не создал дубликаты.
                SaveHistory(messages);
                var note = addedTasks.Count > 0
                    ? $"Задача добавлена ({addedTasks.Count}), но итоговый ответ получить не удалось из-за временного сбоя провайдера."
                    : "Временный сбой провайдера при получении ответа. Попробуйте повторить через минуту.";
                return new ChatResult(note, addedTasks);
            }
            messages.Add(new LlmMessage("assistant", final.Content ?? string.Empty));
            _logger.LogInformation("Chat final answer by {Model}: {Content}",
                final.Model ?? "unknown", Truncate(final.Content, 1000));
            SaveHistory(messages);
            return new ChatResult(final.Content ?? string.Empty, addedTasks);
        }

        messages.Add(new LlmMessage("assistant", response.Content ?? string.Empty));
        _logger.LogInformation("Chat no tool calls, answered with text.");
        SaveHistory(messages);
        return new ChatResult(response.Content ?? string.Empty, addedTasks);
    }

    private List<LlmMessage> LoadHistory()
    {
        if (_session is null)
        {
            return new List<LlmMessage>();
        }

        var json = _session.GetString(HistoryKey);
        if (string.IsNullOrEmpty(json))
        {
            return new List<LlmMessage>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<LlmMessage>>(json, JsonOptions) ?? new List<LlmMessage>();
        }
        catch (JsonException)
        {
            return new List<LlmMessage>();
        }
    }

    private void SaveHistory(List<LlmMessage> messages)
    {
        if (_session is null)
        {
            return;
        }

        var conversational = messages.Skip(1).ToList();
        if (conversational.Count > MaxHistoryMessages)
        {
            conversational = conversational.Skip(conversational.Count - MaxHistoryMessages).ToList();
        }

        _session.SetString(HistoryKey, JsonSerializer.Serialize(conversational, JsonOptions));
    }

    private static List<string>? StripUnjustifiedUrgentTag(List<string>? tags, string userMessage)
    {
        if (tags is null || tags.Count == 0)
        {
            return tags;
        }

        if (!tags.Any(t => t.Equals("Срочно", StringComparison.OrdinalIgnoreCase)))
        {
            return tags;
        }

        if (UrgencyMarkers.Any(m => userMessage.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return tags;
        }

        return tags.Where(t => !t.Equals("Срочно", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static bool LooksLikeAddTaskRequest(string message)
    {
        var lower = message.ToLowerInvariant();
        return TaskIntentMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal));
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? "(пусто)"
        : value.Length <= maxLength ? value
        : value.Substring(0, maxLength) + "…";

    private static LlmMessage ToAssistantMessage(LlmResponse response)
    {
        if (response.ToolCalls.Count > 0)
        {
            return new LlmMessage("assistant", response.Content, ToolCalls: response.ToolCalls);
        }

        return new LlmMessage("assistant", response.Content);
    }
}

public sealed record ChatResult(string Answer, IReadOnlyList<OneTimeTask> AddedTasks);
