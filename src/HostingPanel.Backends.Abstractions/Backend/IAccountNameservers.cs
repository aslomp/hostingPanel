using System.Net.Http.Headers;
namespace HostingPanel.Api;

public sealed record AccountNameserversUpdate(string? Ns1, string? Ns2);
public sealed record AccountNameserversView(string? Ns1, string? Ns2, string? EffectiveNs1, string? EffectiveNs2, bool CanEdit, bool UsesDefaults);

public interface IAccountNameservers
{
    Task<IResult> ReadAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<IResult> WriteAsync(AuthenticationHeaderValue auth, AccountNameserversUpdate update, CancellationToken ct);
}

public static class AccountNameserverValidation
{
    public static bool TryNormalize(AccountNameserversUpdate input, out AccountNameserversUpdate normalized)
    {
        string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('.').ToLowerInvariant();
        var a = Normalize(input.Ns1);
        var b = Normalize(input.Ns2);
        normalized = new(a, b);
        return (a is null && b is null) || (Valid(a) && Valid(b) && a != b);
    }

    private static bool Valid(string? value) => value is { Length: > 0 and <= 253 }
        && value.Contains('.') && value.Split('.').All(label => label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        && !System.Net.IPAddress.TryParse(value, out _);
}
