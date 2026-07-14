using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RatScanner;

internal static class Logger
{
    private static readonly object SyncObject = new();

    private static readonly ConcurrentQueue<string> Backlog = new();
    private static int _processingBacklog;

    private static int _crashed;

    internal static void LogInfo(string message)
    {
        AppendToLog("[Info]  " + message);
    }

    internal static void LogWarning(string message, Exception? e = null)
    {
        AppendToLog("[Warning] " + message);
        if (e != null)
            AppendToLog(e.ToString());
    }

    internal static void LogError(Exception e)
    {
        Exception message = e.GetBaseException().GetBaseException();
        LogError(message.Message, e);
    }

    internal static void LogError(string message, Exception? e = null)
    {
        if (Interlocked.Exchange(ref _crashed, 1) != 0)
            return;

        // Log the error
        string logMessage = "[Error] " + message;
        string divider = new('-', 20);
        if (e != null)
            logMessage += $"\n {divider} \n {e}";
        else
            logMessage += $"\n {divider} \n {Environment.StackTrace}";
        AppendToLog(logMessage);
        Flush();

        try
        {
            string title = RatConfig.FullVersionLabel;
            string faqBoxMessage = message + "\n\nThe FAQ will probably help with that.\nDo you want to open it now?";
            if (
                MessageBox.Show(faqBoxMessage, title, MessageBoxButton.YesNo, MessageBoxImage.Error)
                == MessageBoxResult.Yes
            )
                TryRunSupportAction(() => OpenURL(Constants.Links.FAQ), "Unable to open the FAQ.");

            if (
                MessageBox.Show(
                    "Would you like to create an issue on GitHub?",
                    title,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                ) == MessageBoxResult.Yes
            )
                TryRunSupportAction(() => CreateGitHubIssue(message, e), "Unable to open the GitHub issue form.");
        }
        catch (Exception supportException)
        {
            AppendToLog("[Warning] Unable to display crash assistance.\n" + supportException);
        }
        finally
        {
            Flush();
            Environment.Exit(1);
        }
    }

    internal static void LogDebugBitmap(Bitmap bitmap, string fileName = "bitmap")
    {
        if (RatConfig.LogDebug)
            bitmap.Save(GetUniquePath(RatConfig.Paths.Debug, fileName, ".png"));
    }

    internal static void LogDebug(
        string message = "",
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string caller = ""
    )
    {
        if (!RatConfig.LogDebug)
            return;
        message = $"{caller}:{lineNumber} -> {message}";
        AppendToLog("[Debug] " + message);
    }

    internal static void ShowMessage(string message, string? title = null)
    {
        LogInfo(message);
        MessageBox.Show(message, title ?? RatConfig.FullVersionLabel, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal static void ShowWarning(string message, string? title = null)
    {
        LogWarning(message);
        MessageBox.Show(message, title ?? RatConfig.FullVersionLabel, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string GetUniquePath(string basePath, string fileName, string extension)
    {
        fileName = fileName.Replace(' ', '_');

        int index = 0;
        string uniquePath = Path.Combine(basePath, fileName + index + extension);

        while (File.Exists(uniquePath))
        {
            index += 1;
            uniquePath = Path.Combine(basePath, fileName + index + extension);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(uniquePath) ?? throw new NullReferenceException());
        return uniquePath;
    }

    private static void AppendToLog(string content)
    {
        string text = "[" + DateTime.UtcNow.ToUniversalTime().TimeOfDay + "] > " + content + "\n";
        Backlog.Enqueue(text);
        if (Interlocked.CompareExchange(ref _processingBacklog, 1, 0) == 0)
            _ = Task.Run(ProcessBacklog);
    }

    private static void AppendToLogRaw(string text)
    {
        Debug.WriteLine(text);
        try
        {
            File.AppendAllText(RatConfig.Paths.LogFile, text, Encoding.UTF8);
        }
        catch (Exception exception)
        {
            // Logging must never recursively crash the application. Debug output is
            // still available when the configured log path cannot be written.
            Debug.WriteLine(exception);
        }
    }

    private static void ProcessBacklog()
    {
        while (true)
        {
            lock (SyncObject)
            {
                StringBuilder batch = new();
                while (Backlog.TryDequeue(out string? entry))
                    batch.Append(entry);
                if (batch.Length > 0)
                    AppendToLogRaw(batch.ToString());
            }

            Interlocked.Exchange(ref _processingBacklog, 0);
            if (Backlog.IsEmpty || Interlocked.CompareExchange(ref _processingBacklog, 1, 0) != 0)
                return;
        }
    }

    internal static void Flush()
    {
        lock (SyncObject)
        {
            StringBuilder batch = new();
            while (Backlog.TryDequeue(out string? entry))
                batch.Append(entry);
            if (batch.Length > 0)
                AppendToLogRaw(batch.ToString());
        }
    }

    internal static void Clear()
    {
        lock (SyncObject)
        {
            // Discard queued entries so a pending ProcessBacklog cannot recreate the log
            // with pre-clear content right after we delete it.
            while (Backlog.TryDequeue(out _)) { }
            File.Delete(RatConfig.Paths.LogFile);
        }
    }

    internal static void ClearMats(string pattern = "*.png")
    {
        if (!Directory.Exists(RatConfig.Paths.Data))
            return;

        string[] files = Directory.GetFiles(RatConfig.Paths.Data, pattern);
        foreach (string file in files)
            File.Delete(file);
    }

    internal static void ClearDebugMats()
    {
        if (!Directory.Exists(RatConfig.Paths.Debug))
            return;

        string[] files = Directory.GetFiles(RatConfig.Paths.Debug, "*.png");
        foreach (string file in files)
            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                LogDebug("Exception while deleting debug mats.");
            }
    }

    private static void TryRunSupportAction(Action action, string failureMessage)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            AppendToLog("[Warning] " + failureMessage + "\n" + exception);
        }
    }

    private static void CreateGitHubIssue(string message, Exception? e)
    {
        // Lead with edition + version so fork vs upstream is obvious in the issue list.
        string body = "**Build**\n";
        body += RatConfig.FullVersionLabel + "\n";
        body += "Repo: " + Constants.Links.GitHub + "\n\n";
        body += "**Error**\n" + message + "\n";
        if (e != null)
            body += "```\n" + LimitLength(e.ToString(), 1000) + "\n```\n";

        body += "<details>\n<summary>Log</summary>\n\n```\n";
        body += LimitLength(ReadAll(), 3000);
        body += "\n```\n</details>";

        // Cap the title so the URL-encoded issue link stays within browser/GitHub limits.
        string title = LimitLength($"[{Constants.Branding.EditionToken} {RatConfig.VersionDisplay}] {message}", 120);

        string labels = "bug";

        string url = Constants.Links.GitHub;
        url += "/issues/new";
        url += "?body=" + WebUtility.UrlEncode(body);
        url += "&title=" + WebUtility.UrlEncode(title);
        url += "&labels=" + WebUtility.UrlEncode(labels);

        // Common practical ceiling for opening URLs in Windows shell / browsers.
        // Shrink the body until the fully encoded URL fits (or the body is essentially empty).
        if (url.Length > 2000)
        {
            AppendToLog("[Warning] GitHub issue URL exceeds safe length; truncating body until it fits.");
            int bodyLimit = Math.Min(1500, body.Length);
            while (bodyLimit > 0)
            {
                body = LimitLength(body, bodyLimit);
                url =
                    Constants.Links.GitHub
                    + "/issues/new"
                    + "?body="
                    + WebUtility.UrlEncode(body)
                    + "&title="
                    + WebUtility.UrlEncode(title)
                    + "&labels="
                    + WebUtility.UrlEncode(labels);
                if (url.Length <= 2000)
                    break;
                bodyLimit = Math.Max(0, bodyLimit - 250);
            }
        }

        OpenURL(url);
    }

    private static string LimitLength(string input, int length)
    {
        return input[..Math.Min(length, input.Length)];
    }

    private static void OpenURL(string url)
    {
        ProcessStartInfo psi = new() { FileName = url, UseShellExecute = true };
        Process.Start(psi);
    }

    private static string ReadAll()
    {
        try
        {
            return File.ReadAllText(RatConfig.Paths.LogFile, Encoding.UTF8);
        }
        catch (Exception exception)
        {
            return "The log file could not be read: " + exception.Message;
        }
    }
}
