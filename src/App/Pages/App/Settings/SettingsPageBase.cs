using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace RatScanner;

public abstract class SettingsPageBase : ComponentBase
{
    [Inject]
    protected ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    protected LocalizationService Localizer { get; set; } = null!;

    protected Task ReportAsync(SettingSaveResult result)
    {
        if (!result.Succeeded)
            Snackbar.Add(result.ErrorMessage ?? Localizer["SettingSaveFailed"], Severity.Error);

        return InvokeAsync(StateHasChanged);
    }
}
