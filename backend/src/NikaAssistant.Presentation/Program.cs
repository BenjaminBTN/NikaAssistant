using NikaAssistant.Application.Chat;
using NikaAssistant.Application.CreateTask;
using NikaAssistant.Application.DeleteTask;
using NikaAssistant.Application.GetTask;
using NikaAssistant.Application.UpdateTask;
using NikaAssistant.Contracts;
using NikaAssistant.Infrastructure.LLM;
using NikaAssistant.Infrastructure.LLM.OpenRouter;
using NikaAssistant.Infrastructure.LocalStorage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpClient<OpenRouterClient>();
builder.Services.AddScoped<ILlmClient>(sp => sp.GetRequiredService<OpenRouterClient>());
builder.Services.AddSingleton<IOneTimeTaskStorage, MarkdownOneTimeTaskStorage>();
builder.Services.AddScoped<AddTaskHandler>();
builder.Services.AddScoped<GetTaskHandler>();
builder.Services.AddScoped<DeleteTaskHandler>();
builder.Services.AddScoped<UpdateTaskHandler>();
builder.Services.AddScoped<ChatService>();

var app = builder.Build();

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

app.MapPost("/AddTask", async (AddTaskRequest request, AddTaskHandler handler) =>
{
    await handler.AddTaskAsync(request);
    var created = new OneTimeTask("[ ]", request.Task, request.Assignee, request.Comment ?? "", request.Tags ?? new List<string>());
    return Results.Ok(created);
});

app.MapGet("/GetTasks", async (GetTaskHandler handler) =>
{
    var tasks = await handler.GetTasksAll();
    return Results.Ok(tasks);
});

app.MapPost("/DeleteTask", async (DeleteTaskRequest request, DeleteTaskHandler handler) =>
{
    await handler.DeleteTaskAsync(request);
    return Results.Ok();
});

app.MapPost("/UpdateTask", async (UpdateTaskRequest request, UpdateTaskHandler handler) =>
{
    await handler.UpdateTaskAsync(request);
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
