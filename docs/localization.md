# Localization

HostingPanel.Web uses .NET `.resx` resources and `IStringLocalizer<SharedResource>`. Default culture: `nl-NL`; supported: `nl-NL`, `en-US`. The header language selector stores the user's choice in the ASP.NET Core culture cookie and reloads the current page. If there is no cookie, `Accept-Language` is used.

Resources live in `src/HostingPanel.Web/Resources/Localization.SharedResource.resx` (Dutch neutral fallback) and `Localization.SharedResource.en-US.resx` (English). The marker type is `HostingPanel.Web.Localization.SharedResource`. Add matching `<data name=...>` entries for every new UI string. Use `@inject IStringLocalizer<SharedResource> L` and `@L["Text"]` in Razor. The API, Swagger paths, and backend identifiers must never be translated. Avoid storing user language globally: culture is scoped per request/circuit.

The shared application shell, navigation and home dashboard have been migrated. Other pages still contain literal Dutch text and need gradual migration to shared resources. The fallback remains Dutch until those are extracted.
