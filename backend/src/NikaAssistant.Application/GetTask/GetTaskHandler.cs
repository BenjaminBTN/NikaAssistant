using NikaAssistant.Contracts;

namespace NikaAssistant.Application.GetTask;

public sealed class GetTaskHandler
{
    private const string FilePath = @"C:\Users\galki\Storage\Tasks\OneTime\one-time-tasks.md";

    public Task<IReadOnlyList<OneTimeTask>> GetTasksAll(CancellationToken cancellationToken = default)
    {
        var tasks = new List<OneTimeTask>();

        if (!File.Exists(FilePath))
        {
            return Task.FromResult<IReadOnlyList<OneTimeTask>>(tasks);
        }

        var pastHeader = false;

        foreach (var line in File.ReadAllLines(FilePath))
        {
            if (!line.StartsWith("|", StringComparison.Ordinal))
            {
                pastHeader = false;
                continue;
            }

            if (line.Contains("---", StringComparison.Ordinal))
            {
                pastHeader = true;
                continue;
            }

            if (!pastHeader)
            {
                continue;
            }

            var cells = line.Split('|');
            if (cells.Length < 6)
            {
                continue;
            }

            var status = cells[1].Trim();
            var task = cells[2].Trim();
            var assignee = cells[3].Trim();
            var comment = cells[4].Trim();

            if (string.IsNullOrWhiteSpace(task))
            {
                continue;
            }

            tasks.Add(new OneTimeTask(status, task, assignee, comment));
        }

        return Task.FromResult<IReadOnlyList<OneTimeTask>>(tasks);
    }
}
