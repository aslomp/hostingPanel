using MudBlazor;

namespace HostingPanel.Web.Components;

public static class DestructiveResourceDialogs
{
    public static async Task<bool> ConfirmResourceDeletionAsync(this IDialogService dialogs,
        string title, string resourceName, string warning, string prompt, string description = "")
    {
        if (string.IsNullOrWhiteSpace(resourceName)) return false;
        var parameters = new DialogParameters<DestructiveResourceDialog>
        {
            { x => x.ResourceName, resourceName },
            { x => x.Warning, warning },
            { x => x.Description, description },
            { x => x.Prompt, prompt }
        };
        var reference = await dialogs.ShowAsync<DestructiveResourceDialog>(
            title, parameters, new DialogOptions
            {
                CloseOnEscapeKey = true, BackdropClick = false,
                MaxWidth = MaxWidth.Small, FullWidth = true
            });
        return await reference.Result is { Canceled: false, Data: true };
    }
}
