using System;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Web.WebView2.Core;

namespace RatScanner.View;

internal static class WebView2TestEnvironment
{
    private const string PortVariable = "RATSCANNER_UI_TEST_CDP_PORT";
    private const string ProfileVariable = "RATSCANNER_UI_TEST_PROFILE";

    public static void Apply(BlazorWebViewInitializingEventArgs args)
    {
        string? portText = Environment.GetEnvironmentVariable(PortVariable);
        string? profileDirectory = Environment.GetEnvironmentVariable(ProfileVariable);
        if (portText is null && profileDirectory is null)
            return;

        if (
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 0 or > 65535
        )
            throw new InvalidOperationException($"{PortVariable} must contain zero or a valid TCP port.");
        if (string.IsNullOrWhiteSpace(profileDirectory) || !Path.IsPathFullyQualified(profileDirectory))
            throw new InvalidOperationException($"{ProfileVariable} must contain a fully qualified directory path.");

        args.UserDataFolder = profileDirectory;
        args.EnvironmentOptions = new CoreWebView2EnvironmentOptions(
            additionalBrowserArguments: $"--remote-debugging-port={port}"
        );
    }
}
