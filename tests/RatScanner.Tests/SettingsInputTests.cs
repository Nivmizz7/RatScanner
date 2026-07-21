#nullable enable

using RatScanner.ViewModel;
using Xunit;

namespace RatScanner.Tests;

public sealed class SettingsInputTests
{
    [Theory]
    [InlineData("100", 1f)]
    [InlineData("125%", 1.25f)]
    [InlineData(" 150 % ", 1.5f)]
    [InlineData("62.5", 0.625f)]
    public void Display_scale_percentage_accepts_valid_user_input(string text, float expected)
    {
        bool parsed = SettingsVM.TryParseDisplayScalePercentage(text, out float actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual, precision: 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("Infinity")]
    public void Display_scale_percentage_rejects_invalid_input_instead_of_silently_using_100_percent(string text)
    {
        Assert.False(SettingsVM.TryParseDisplayScalePercentage(text, out _));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("1920", 1920)]
    [InlineData(" 2560 ", 2560)]
    public void Positive_integer_input_accepts_complete_valid_values(string text, int expected)
    {
        Assert.True(SettingsVM.TryParsePositiveInt(text, out int actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("999999999999")]
    [InlineData("12.5")]
    public void Positive_integer_input_rejects_invalid_or_overflowing_values(string text)
    {
        Assert.False(SettingsVM.TryParsePositiveInt(text, out _));
    }
}
