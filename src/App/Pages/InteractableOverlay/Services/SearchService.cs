using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RatScanner.TarkovDev;
using Tasks = System.Threading.Tasks;
using TTask = RatScanner.TarkovDev.Task;

namespace RatScanner.Pages.InteractableOverlay.Services;

public class SearchService
{
    public Tasks.Task<IEnumerable<SearchResult>> SearchMapsAsync(
        string value,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(value))
            return Tasks.Task.FromResult(Enumerable.Empty<SearchResult>());

        return Tasks.Task.Run<IEnumerable<SearchResult>>(
            () =>
            {
                List<SearchResult> matches = new();
                foreach (var map in TarkovDevAPI.GetMaps())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = SanitizeSearch(map.Name);
                    SearchResult? match =
                        name == value ? new(map, 3)
                        : name.StartsWith(value, StringComparison.Ordinal) ? new(map, 15)
                        : name.Contains(value, StringComparison.Ordinal) ? new(map, 45)
                        : null;
                    if (match != null)
                        matches.Add(match);
                }
                return matches;
            },
            cancellationToken
        );
    }

    public Tasks.Task<IEnumerable<SearchResult>> SearchTasksAsync(
        string value,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(value))
            return Tasks.Task.FromResult(Enumerable.Empty<SearchResult>());

        string[] filters = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Tasks.Task.Run<IEnumerable<SearchResult>>(
            () =>
            {
                List<SearchResult> matches = new();
                foreach (var task in TarkovDevAPI.GetTasks())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = SanitizeSearch(task.Name);
                    string id = SanitizeSearch(task.Id);
                    SearchResult? match =
                        name == value ? new(task, 4)
                        : name.StartsWith(value, StringComparison.Ordinal) ? new(task, 10)
                        : filters.All(filter => name.Contains(filter, StringComparison.Ordinal)) ? new(task, 30)
                        : name.Contains(value, StringComparison.Ordinal) ? new(task, 50)
                        : value.Length > 3 && id.StartsWith(value, StringComparison.Ordinal) ? new(task, 80)
                        : value.Length > 3 && id.Contains(value, StringComparison.Ordinal) ? new(task, 100)
                        : null;
                    if (match != null)
                        matches.Add(match);
                }
                return matches;
            },
            cancellationToken
        );
    }

    public Tasks.Task<IEnumerable<SearchResult>> SearchItemsAsync(
        string value,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(value))
            return Tasks.Task.FromResult(Enumerable.Empty<SearchResult>());

        string[] filters = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Tasks.Task.Run<IEnumerable<SearchResult>>(
            () =>
            {
                List<SearchResult> matches = new();
                foreach (var item in TarkovDevAPI.GetItems())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = SanitizeSearch(item.Name);
                    string shortName = SanitizeSearch(item.ShortName);
                    string id = SanitizeSearch(item.Id);

                    SearchResult? match =
                        name == value ? new(item, 5)
                        : shortName == value ? new(item, 10)
                        : name.StartsWith(value, StringComparison.Ordinal) ? new(item, 20)
                        : shortName.StartsWith(value, StringComparison.Ordinal) ? new(item, 20)
                        : filters.All(filter => name.Contains(filter, StringComparison.Ordinal)) ? new(item, 40)
                        : filters.All(filter => shortName.Contains(filter, StringComparison.Ordinal)) ? new(item, 40)
                        : name.Contains(value, StringComparison.Ordinal) ? new(item, 60)
                        : shortName.Contains(value, StringComparison.Ordinal) ? new(item, 60)
                        : value.Length > 3 && id.StartsWith(value, StringComparison.Ordinal) ? new(item, 80)
                        : value.Length > 3 && id.Contains(value, StringComparison.Ordinal) ? new(item, 100)
                        : null;
                    if (match == null)
                        continue;

                    match.Score += (item.Name?.Length ?? 0) * 0.002;
                    if (item.Types?.Any(t => string.Equals(t, "mods", StringComparison.OrdinalIgnoreCase)) == true)
                        match.Score += 5;
                    matches.Add(match);
                }
                return matches;
            },
            cancellationToken
        );
    }

    public string SanitizeSearch(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        value = value.ToLowerInvariant().Trim();
        value = value.Replace("-", " ");
        value = new string(value.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
        return value;
    }
}

public class SearchResult
{
    public SearchResult(object data, float score)
    {
        Score = score;
        Data = data;
    }

    public object Data;
    public double Score;
}
