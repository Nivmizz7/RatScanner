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
}
