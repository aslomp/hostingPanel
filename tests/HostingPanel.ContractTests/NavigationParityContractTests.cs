using System.Text.RegularExpressions;

public static class NavigationParityContractTests
{
    public static void Run()
    {
        var root = FindRoot();
        var reference = File.ReadAllText(Path.Combine(root, "index.html"));
        var navigation = File.ReadAllText(Path.Combine(root,
            "src/HostingPanel.Web/Components/Layout/MainLayout.razor"));
        var placeholder = File.ReadAllText(Path.Combine(root,
            "src/HostingPanel.Web/Components/Pages/NavigationPage.razor"));
        var pages = Path.Combine(root, "src/HostingPanel.Web/Components/Pages");

        // Exact reference sections, in reference order; no user-role inference.
        string[] headings = [
            "Accountbeheer", "Server Beheer", "Admin Gereedschap",
            "Systeem Info & Bestanden", "Extra Programma's", "Support", "Gebruiker"
        ];
        var last = -1;
        foreach (var title in headings)
        {
            var token = "new(\"" + title + "\"";
            var next = navigation.IndexOf(token, StringComparison.Ordinal);
            Require(next > last, "Missing or reordered reference group: " + title);
            last = next;
            Require(reference.Contains(title, StringComparison.Ordinal),
                "Reference changed group: " + title);
        }

        var declared = Regex.Matches(navigation,
            @"(?:Live|Planned)\(""(?<label>[^""]+)"",\s*""[^""]+"",\s*""(?<path>[^""]+)""\)");
        Require(declared.Count >= 50, "Expected all reference navigation entries");
        var sectionStart = reference.IndexOf("const sections =", StringComparison.Ordinal);
        var sectionEnd = reference.IndexOf("const expandedNavGroups =", StringComparison.Ordinal);
        if (sectionEnd < 0) sectionEnd = reference.Length;
        Require(sectionStart >= 0 && sectionEnd > sectionStart,
            "Reference index.html has no recognizable section definition");
        var referenceSectionText = reference[sectionStart..sectionEnd];
        var paths = Directory.GetFiles(pages, "*.razor")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path),
                @"@page ""(?<route>/[^""]+)""").Select(x => x.Groups["route"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Parameter names are irrelevant for Blazor route matching:
        // /navigation/{Key} and /navigation/{Feature} would be ambiguous.
        var routeOwners = Directory.GetFiles(pages, "*.razor")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file),
                @"@page ""(?<route>/[^""]+)""")
                .Select(match => (File: Path.GetFileName(file),
                    Route: match.Groups["route"].Value)))
            .ToArray();
        var ambiguities = routeOwners.GroupBy(entry =>
            Regex.Replace(entry.Route.ToLowerInvariant(), @"\\{[^}]+\\}", "{}"))
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(entry =>
                entry.File + ": " + entry.Route)))
            .ToArray();
        Require(ambiguities.Length == 0, "Ambiguous Blazor routes: " +
            string.Join(" | ", ambiguities));

        var hostingPanelExtras = new HashSet<string>(StringComparer.Ordinal)
        {
            "Backend instellingen", "Sessies", "Resellerbeheer", "Databasebeheer", "Systeeminformatie",
            "ModSecurity en ClamAV", "DirectAdmin API",
            "Tickets", "Nieuw ticket", "Berichten", "Wachtwoord wijzigen"
        };
        foreach (Match match in declared)
        {
            var label = match.Groups["label"].Value;
            var destination = match.Groups["path"].Value;
            Require(hostingPanelExtras.Contains(label) ||
                referenceSectionText.Contains("\"" + label + "\"", StringComparison.Ordinal),
                "Navigation label absent in index.html: " + label);
            if (match.Value.StartsWith("Live(", StringComparison.Ordinal))
                Require(paths.Contains(destination), "Missing implemented page route: " + destination);
            else
                Require(placeholder.Contains("[\"" + destination + "\"]", StringComparison.Ordinal),
                    "Unmapped prototype-only menu item: " + destination);
        }

        Require(navigation.Contains("private static readonly NavSection[] AdminSections", StringComparison.Ordinal) &&
                navigation.Contains("private static readonly NavSection[] UserSections", StringComparison.Ordinal) &&
                navigation.Contains("NavContext.Mode == \"user\" ? UserSections : AdminSections",
                    StringComparison.Ordinal),
            "Administrator and user navigation must be separate presentation contexts.");
        Require(navigation.Contains("SetContext", StringComparison.Ordinal) &&
                navigation.Contains("aria-pressed", StringComparison.Ordinal),
            "Manual navigation context selector is required.");
        Require(!navigation.Contains("Session.Role", StringComparison.Ordinal) &&
                !navigation.Contains("isAdmin", StringComparison.Ordinal),
            "Navigation must not perform local account-role authorization");
        Require(navigation.Contains("href=\"/\"", StringComparison.Ordinal),
            "Dashboard link missing");
    }

    private static string FindRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "index.html")) &&
                Directory.Exists(Path.Combine(current.FullName, "src", "HostingPanel.Web")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
