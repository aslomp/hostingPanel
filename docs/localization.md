# Localization

English (`en-US`) is the default and neutral fallback. Dutch (`nl-NL`) translations live in `src/HostingPanel.Web/Resources/Localization.SharedResource.nl-NL.resx`. Use English resource keys in Razor with `IStringLocalizer<SharedResource>`. Keep API routes, identifiers and backend contracts language-neutral. The culture cookie takes precedence over Accept-Language. Changing culture reloads the Blazor circuit.
