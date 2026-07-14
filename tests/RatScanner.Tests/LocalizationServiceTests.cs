#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Xunit;

namespace RatScanner.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void Packaged_locale_catalogs_have_the_exact_english_key_set()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "i18n");
        string[] files = Directory.GetFiles(directory, "*.json");
        Assert.Equal(Enum.GetValues<UiLanguage>().Length, files.Length);

        HashSet<string> baseline = ReadKeys(Path.Combine(directory, "en.json"));
        foreach (string file in files)
        {
            HashSet<string> actual = ReadKeys(file);
            Assert.True(
                baseline.SetEquals(actual),
                $"{Path.GetFileName(file)} key drift. Missing: {string.Join(", ", baseline.Except(actual))}. "
                    + $"Extra: {string.Join(", ", actual.Except(baseline))}."
            );
        }
    }

    [Fact]
    public void Missing_selected_locale_falls_back_to_english()
    {
        using TemporaryDirectory directory = new();
        directory.Write("en.json", """{ "Greeting": "Hello" }""");

        LocalizationService service = new(directory.Path, UiLanguage.Spanish);

        Assert.Equal("Hello", service["Greeting"]);
    }

    [Fact]
    public void Malformed_selected_locale_does_not_retain_the_previous_language()
    {
        using TemporaryDirectory directory = new();
        directory.Write("en.json", """{ "Greeting": "Hello" }""");
        directory.Write("es.json", """{ "Greeting": "Hola" }""");
        LocalizationService service = new(directory.Path, UiLanguage.Spanish);
        Assert.Equal("Hola", service["Greeting"]);

        directory.Write("es.json", "{ invalid json");
        service.SetLanguage(UiLanguage.Spanish);

        Assert.Equal("Hello", service["Greeting"]);
    }

    [Fact]
    public void Missing_key_in_selected_locale_falls_back_to_english_then_to_key()
    {
        using TemporaryDirectory directory = new();
        directory.Write("en.json", """{ "EnglishOnly": "Fallback" }""");
        directory.Write("es.json", """{ "SpanishOnly": "Español" }""");

        LocalizationService service = new(directory.Path, UiLanguage.Spanish);

        Assert.Equal("Fallback", service["EnglishOnly"]);
        Assert.Equal("UnknownKey", service["UnknownKey"]);
    }

    private static HashSet<string> ReadKeys(string path)
    {
        Dictionary<string, string>? catalog = JsonConvert.DeserializeObject<Dictionary<string, string>>(
            File.ReadAllText(path)
        );
        Assert.NotNull(catalog);
        return new HashSet<string>(catalog.Keys, StringComparer.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RatScanner-i18n-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string name, string contents) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), contents);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
