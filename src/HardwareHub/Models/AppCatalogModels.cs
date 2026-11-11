using System.Text.Json.Serialization;

namespace HardwareHub.Models;

/// <summary>
/// Acao de ajuste de idioma executada apos a instalacao de um pacote (registro ou copia de arquivo).
/// </summary>
public sealed class AppLanguageAction
{
    [JsonPropertyName("tipo")]
    public string Type { get; set; } = "reg";

    [JsonPropertyName("chave")]
    public string? Key { get; set; }

    [JsonPropertyName("valor")]
    public string? ValueName { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("dados")]
    public string? Data { get; set; }

    [JsonPropertyName("de")]
    public string? FromPath { get; set; }

    [JsonPropertyName("para")]
    public string? ToPath { get; set; }
}

/// <summary>
/// Definicao de programa do catalogo curado (compativel com posformatacao.json do HUB Pro).
/// </summary>
public sealed class AppPackageDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("categoria")]
    public string Category { get; set; } = "";

    [JsonPropertyName("nome")]
    public string Name { get; set; } = "";

    [JsonPropertyName("descricao")]
    public string Description { get; set; } = "";

    [JsonPropertyName("winget")]
    public string WingetId { get; set; } = "";

    [JsonPropertyName("fonte")]
    public string Source { get; set; } = "winget";

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("nomeInstalado")]
    public string InstalledName { get; set; } = "";

    [JsonPropertyName("pacoteAppx")]
    public string? AppxPackage { get; set; }

    [JsonPropertyName("idioma")]
    public string LanguageMode { get; set; } = "windows";

    [JsonPropertyName("ajustesIdioma")]
    public List<AppLanguageAction> LanguageActions { get; set; } = new();

    [JsonPropertyName("marcado")]
    public bool DefaultChecked { get; set; }

    [JsonPropertyName("usuario")]
    public bool UserScope { get; set; }

    [JsonPropertyName("icone")]
    public string? IconName { get; set; }

    [JsonPropertyName("glifo")]
    public string? GlyphHex { get; set; }

    [JsonPropertyName("minutos")]
    public double EstimatedMinutes { get; set; } = 1.0;

    [JsonPropertyName("tipo")]
    public string Type { get; set; } = "winget";

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("cupom")]
    public string? Coupon { get; set; }

    [JsonPropertyName("destaque")]
    public string? HighlightText { get; set; }

    [JsonPropertyName("dica")]
    public string? Tip { get; set; }

    [JsonIgnore]
    public bool IsLink => Type.Equals("link", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string EffectiveIconName => string.IsNullOrWhiteSpace(IconName) ? Id : IconName;
}

/// <summary>
/// Definicao de kit pronto no catalogo de programas.
/// </summary>
public sealed class AppKitDefinition
{
    [JsonPropertyName("nome")]
    public string Name { get; set; } = "";

    [JsonPropertyName("descricao")]
    public string Description { get; set; } = "";

    [JsonPropertyName("icone")]
    public string? IconGlyphHex { get; set; }

    [JsonPropertyName("programas")]
    public List<string> ProgramIds { get; set; } = new();
}

/// <summary>
/// Estrutura raiz do arquivo posformatacao.json.
/// </summary>
public sealed class AppCatalogDocument
{
    [JsonPropertyName("kits")]
    public List<AppKitDefinition> Kits { get; set; } = new();

    [JsonPropertyName("programas")]
    public List<AppPackageDefinition> Programs { get; set; } = new();
}

/// <summary>
/// Pacote retornado por busca ao vivo no Winget ou na Microsoft Store.
/// </summary>
public sealed record WingetSearchPackage(string Name, string Id, string Version, string Source);

/// <summary>
/// Resultado consolidado de busca online no Winget + Microsoft Store.
/// </summary>
public sealed record WingetSearchResponse(
    List<WingetSearchPackage> Winget,
    List<WingetSearchPackage> Store,
    int TotalWinget,
    int HiddenOtherLocales,
    string Error);

/// <summary>
/// Metadados detalhados obtidos sob demanda via winget show.
/// </summary>
public sealed record WingetPackageDetails(
    string Publisher,
    string Description,
    bool InstallerHasPreferredLocale,
    string? Homepage = null,
    string? PublisherUrl = null);

/// <summary>
/// Resultado estruturado de uma execucao oficial do Winget.
/// </summary>
public sealed record AppInstallOutcome(
    bool Success,
    int ExitCode,
    string ShortMessage,
    string FullOutput);

