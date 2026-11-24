namespace HostingPanel.Api;

public static class HostingInputValidation
{
    public static bool ValidUsername(string? username) =>
        !string.IsNullOrWhiteSpace(username) && username.Length <= 64
        && username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public static bool ValidOwner(string? owner) => owner is null || ValidUsername(owner);
}
