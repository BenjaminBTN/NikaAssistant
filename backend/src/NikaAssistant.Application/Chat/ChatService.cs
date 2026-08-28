using NikaAssistant.Contracts;
using NikaAssistant.Infrastructure.LLM;
using NikaAssistant.Infrastructure.LocalStorage;
using System.Text.Json;

namespace NikaAssistant.Application.Chat;

public sealed class ChatService
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private static readonly string[] TaskTriggers =
    {
        "добав", "созда", "задач", "запис", "добавить", "новую задач", "сформируй", "запланируй", "напомни"
    };

    private static readonly LlmTool AddTaskTool = new(
        "add_task",
        "Добавить новую разовую задачу в список задач пользователя. Используй, когда пользователь просит создать или добавить задачу.",
        """
        {
          "type": "object",
          "properties": {
            "task": { "type": "string", "description": "Текст задачи" },
            "assignee": { "type": "string", "description": "Ответственный за задачу" },
            "comment": { "type": "string", "description": "Комментарий к задаче" }
          },
          "required": ["task"]
        }
        """);

    private readonly ILlmClient _llm;
    private readonly IOneTimeTaskStorage _storage;

    public ChatService(ILlmClient llm, IOneTimeTaskStorage storage)
    {
        _llm = llm;
        _storage = storage;
    }

    public async Task<ChatResult> AskAsync(string message, CancellationToken cancellationToken = default)
    {
        var messages = new List<LlmMessage>
        {
            new("system", "Ты — помощник Nika. Общайся на русском. Инструмент add_task вызывай СТРОГО только когда пользователь явно просит добавить, создать или записать задачу. Во всех остальных случаях (вопросы, болтовня, уточнения) просто отвечай текстом и никаких задач не создавай."),
            new("user", message)
        };

        if (!LooksLikeTaskRequest(message))
        {
            var plain = await _llm.CompleteAsync(messages, null, cancellationToken);
            return new ChatResult(plain.Content ?? string.Empty, Array.Empty<OneTimeTask>());
        }

        var response = await _llm.CompleteAsync(messages, new[] { AddTaskTool }, cancellationToken);
        messages.Add(ToAssistantMessage(response));

        var addedTasks = new List<OneTimeTask>();

        foreach (var call in response.ToolCalls)
        {
            if (call.Name == "add_task")
            {
                var request = JsonSerializer.Deserialize<AddTaskRequest>(call.ArgumentsJson, JsonOptions);
                if (request is not null)
                {
                    await _storage.AddAsync(request, cancellationToken);
                    addedTasks.Add(new OneTimeTask("[ ]", request.Task, request.Assignee, request.Comment ?? ""));
                    messages.Add(new LlmMessage("tool", $"Задача успешно добавлена: {request.Task}", call.Id));
                }
                else
                {
                    messages.Add(new LlmMessage("tool", "Не удалось разобрать аргументы задачи.", call.Id));
                }
            }
        }

        if (response.ToolCalls.Count > 0)
        {
            var final = await _llm.CompleteAsync(messages, null, cancellationToken);
            return new ChatResult(final.Content ?? string.Empty, addedTasks);
        }

        return new ChatResult(response.Content ?? string.Empty, addedTasks);
    }

    private static bool LooksLikeTaskRequest(string message) =>
        TaskTriggers.Any(t => message.Contains(t, StringComparison.OrdinalIgnoreCase));

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
