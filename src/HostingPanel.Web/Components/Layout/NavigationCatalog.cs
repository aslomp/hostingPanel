namespace HostingPanel.Web.Components.Layout;

internal static class NavigationCatalog
{
    internal sealed record NavItem(string Label, string Icon, string Path, bool Implemented = true);
    internal sealed record NavSection(string Title, string Icon, NavItem[] Items);

    private static NavItem Live(string label, string icon, string path) => new(label, icon, path);
    private static NavItem Planned(string label, string icon, string key) =>
        new(label, icon, "/navigation/" + key);

    // Match the section and item order in the reference index.html.
    // All items remain visible for every account: DirectAdmin authorizes each request.
    internal static readonly NavSection[] AllSections =
    [
        new("Account management", "♙",
        [
            Live("Add new user", "♙", "/users"),
            Live("Show all users", "♧", "/users"),
            Live("My users", "♙", "/users"),
            Live("Manage user packages", "▤", "/user-packages"),
            Planned("Move users between resellers", "⇄", "move-users"),
            Planned("Message templates", "✉", "message-templates"),
            Live("Change passwords", "⚿", "/change-password"),
            Live("Create reseller", "♙", "/resellers"),
            Live("Resellers", "♙", "/resellers"),
            Live("Manage reseller packages", "▤", "/reseller-packages"),
            Planned("Create administrator", "♙", "create-admin"),
            Live("Administrators", "♙", "/users")
        ]),
        new("Server management", "▣",
        [
            Live("Administrator settings", "⚙", "/server-settings"),
            Live("Backend settings", "⚙", "/backend-settings"),
            Planned("Custom HTTPD configurations", "▤", "httpd-config"),
            Live("DNS management", "◎", "/dns-zones"),
            Planned("IP management", "⌁", "ip-management"),
            Planned("Name servers", "◇", "name-servers"),
            Planned("Multi-server setup", "▦", "multi-server"),
            Planned("PHP settings", "⌘", "php-settings"),
            Live("Server TLS certificate", "♢", "/server-tls"),
            Planned("CustomBuild", "⚒", "custombuild")
        ]),
        new("Admin tools", "⚒",
        [
            Planned("Backup and restore", "◷", "backup-restore"),
            Planned("Brute force monitor", "♢", "brute-force-monitor"),
            Planned("Process monitor", "▥", "process-monitor"),
            Planned("Manage mail queue", "✉", "mail-queue"),
            Live("System services", "⚙", "/system-services"),
            Planned("System backup", "▣", "system-backup"),
            Planned("Import cPanel", "⇩", "cpanel-import"),
            Planned("Message all users", "✉", "broadcast"),
            Planned("Themes", "◈", "themes"),
            Planned("Customize Evolution theme", "◈", "evolution-theme"),
            Planned("Maintenance", "⚒", "maintenance")
        ]),
        new("System information & files", "▤",
        [
            Planned("All users' cron jobs", "◷", "global-cron"),
            Planned("System file editor", "▤", "system-file-editor"),
            Planned("View logs", "☷", "system-logs"),
            Live("Global statistics", "◴", "/system-info")
        ]),
        new("Extra programs", "▦",
        [
            Planned("Plugin management", "▦", "plugin-manager"),
            Planned("ConfigServer Security & Firewall", "♢", "csf")
        ]),
        new("Support", "?",
        [
            Live("Manage tickets", "☏", "/tickets"),
            Live("Create ticket", "✉", "/tickets/new"),
            Live("Updates", "↻", "/system-updates"),
            Planned("Licensing", "♧", "licensing")
        ]),
        new("Additional HostingPanel features", "⌘",
        [
            Live("Sessions", "◷", "/sessions"),
            Live("Reseller management", "♙", "/resellers"),
            Live("Database management", "▦", "/databases"),
            Live("Email accounts", "✉", "/email-accounts"),
            Live("System information", "▤", "/system-info"),
            Live("ModSecurity and ClamAV", "♢", "/security"),
            Live("DirectAdmin API", "⌘", "/api-browser")
        ])
    ];

    internal static readonly NavSection[] AdminSections =
        AllSections.Where(section => section.Title != "User").ToArray();

    internal static readonly NavSection[] UserSections =
    [
        new("User", "♙",
        [
            Live("Domain management", "◎", "/domains"),
            Live("Email accounts", "✉", "/email-accounts"),
            Planned("File management", "▤", "file-manager"),
            Live("Databases", "▦", "/databases"),
            Planned("SSL certificates", "♢", "domain-ssl"),
            Live("DNS management", "◎", "/dns-zones"),
            Planned("Cron jobs", "◷", "cron-jobs"),
            Planned("Backups", "◷", "user-backups")
        ]),
        new("Support", "☏",
        [
            Live("Tickets", "☏", "/tickets"),
            Live("New ticket", "✉", "/tickets/new"),
            Live("Messages", "✉", "/messages"),
            Live("Change password", "⚿", "/change-password")
        ])
    ];

}
