using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HardwareHub.Services;

/// <summary>
/// Carrega os icones PNG oficiais embutidos do catalogo e busca/cacheia icones dinamicos para resultados do Winget/Store.
/// </summary>
public static class AppIconService
{
    private const int MaxIconBytes = 524_288;
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(30);

    private static readonly ConcurrentDictionary<string, ImageSource?> EmbeddedCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource?> OnlineCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> MissingOnlineIcons = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly Regex StoreIdRegex = new("^[A-Za-z0-9]{12}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeCacheKeyRegex = new("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant);
    private static readonly Regex LinkTagRegex = new("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HrefAttrRegex = new("\\bhref\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RelAttrRegex = new("\\brel\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DiskCacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HardwareHub",
        "icones-programas");

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HardwareHub/1.0");
        return client;
    }

    /// <summary>
    /// Carrega o icone PNG oficial embutido em Resources/AppIcons/{name}.png, ou null caso nao exista.
    /// </summary>
    public static ImageSource? LoadEmbeddedIcon(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        if (EmbeddedCache.TryGetValue(iconName, out var cached))
        {
            return cached;
        }

        ImageSource? loaded = null;
        try
        {
            EnsurePackSchemeRegistered();
            var uri = new Uri($"pack://application:,,,/HardwareHub;component/Resources/AppIcons/{iconName}.png", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                loaded = bmp;
            }
        }
        catch
        {
            loaded = null;
        }

        EmbeddedCache[iconName] = loaded;
        return loaded;
    }

    private static void EnsurePackSchemeRegistered()
    {
        // Em testes unitarios WPF STA sem App.MainWindow ativo, garante que o esquema pack:// foi inicializado.
        if (!UriParser.IsKnownScheme("pack"))
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        }
    }

    /// <summary>
    /// Converte codigo hexadecimal (ex: E734) para o glifo da fonte Segoe MDL2 Assets / Segoe Fluent Icons.
    /// </summary>
    public static string ResolveGlyph(string? hexCode, string fallbackGlyph)
    {
        if (!string.IsNullOrWhiteSpace(hexCode) &&
            int.TryParse(hexCode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
        {
            return char.ConvertFromUtf32(codePoint);
        }

        return fallbackGlyph;
    }

    /// <summary>
    /// Retorna o glifo Segoe correspondente a categoria (identico ao HUB Pro).
    /// </summary>
    public static string GetCategoryGlyph(string category) => category switch
    {
        "Segurança" => "\uE83D",
        "Navegadores" => "\uE774",
        "Compactadores" => "\uF012",
        "Vídeo e música" => "\uE8D6",
        "Criação de vídeo" => "\uE714",
        "Streaming" => "\uE7F4",
        "Documentos" => "\uE8A5",
        "Comunicação" => "\uE8BD",
        "Inteligência artificial" or "IA" => "\uE99A",
        "Jogos" => "\uE7FC",
        "Personalização" => "\uE771",
        "Diagnóstico e disco" or "Diagnóstico" => "\uE9D9",
        "Pendrive bootável" => "\uE88E",
        "Programação" => "\uE943",
        "Base para jogos e programas" or "Bibliotecas" => "\uE74C",
        _ => "\uE71D"
    };

    /// <summary>
    /// Retorna o icone online ja em memoria se existir.
    /// </summary>
    public static ImageSource? TryGetCachedOnlineIcon(string packageId)
    {
        return OnlineCache.TryGetValue(packageId, out var img) ? img : null;
    }

    /// <summary>
    /// Busca e cacheia em disco o icone oficial de um pacote obtido via busca dinamica no Winget ou Microsoft Store.
    /// </summary>
    public static async Task<ImageSource?> FetchOnlineIconAsync(
        string packageId,
        string source,
        string? homepageUrl,
        string? publisherUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        if (OnlineCache.TryGetValue(packageId, out var memCached))
        {
            return memCached;
        }

        if (MissingOnlineIcons.ContainsKey(packageId))
        {
            return null;
        }

        string diskFile = GetDiskCacheFilePath(packageId);
        var fromDisk = TryLoadFromDiskCache(diskFile);
        if (fromDisk != null)
        {
            OnlineCache[packageId] = fromDisk;
            return fromDisk;
        }

        ImageSource? downloaded = null;
        if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase) && StoreIdRegex.IsMatch(packageId))
        {
            downloaded = await FetchStoreLogoAsync(packageId, diskFile, ct);
        }
        else
        {
            downloaded = await FetchWebsiteFaviconAsync(homepageUrl, publisherUrl, diskFile, ct);
        }

        if (downloaded != null)
        {
            OnlineCache[packageId] = downloaded;
        }
        else
        {
            MissingOnlineIcons[packageId] = 1;
        }

        return downloaded;
    }

    private static string GetDiskCacheFilePath(string packageId)
    {
        string safe = SafeCacheKeyRegex.Replace(packageId, "_");
        if (safe.Length > 80)
        {
            safe = safe[..80];
        }

        return Path.Combine(DiskCacheFolder, $"{safe}.png");
    }

    private static ImageSource? TryLoadFromDiskCache(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > DiskCacheTtl)
            {
                return null;
            }

            return DecodeImageBytes(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> FetchStoreLogoAsync(string productId, string diskFile, CancellationToken ct)
    {
        try
        {
            string url = $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/products/{productId}?market=BR&locale=pt-BR&deviceFamily=Windows.Desktop";
            string json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Payload", out var payload) ||
                !payload.TryGetProperty("Images", out var images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestUrl = null;
            int bestScore = int.MaxValue;
            foreach (var img in images.EnumerateArray())
            {
                string type = img.TryGetProperty("ImageType", out var tProp) ? tProp.GetString() ?? "" : "";
                string candidate = img.TryGetProperty("Uri", out var uProp) ? uProp.GetString() ?? "" : "";
                if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int score = type.ToLowerInvariant() switch
                {
                    "logo" => 0,
                    "tile" => 1,
                    "boxart" => 2,
                    _ => 3
                };

                if (score < bestScore)
                {
                    bestScore = score;
                    bestUrl = candidate;
                }
            }

            if (string.IsNullOrWhiteSpace(bestUrl))
            {
                return null;
            }

            return await DownloadAndCacheImageAsync(bestUrl, diskFile, ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> FetchWebsiteFaviconAsync(
        string? homepageUrl,
        string? publisherUrl,
        string diskFile,
        CancellationToken ct)
    {
        foreach (string? site in new[] { homepageUrl, publisherUrl })
        {
            if (!TryCreateHttpsUri(site, out var baseUri) || baseUri == null)
            {
                continue;
            }

            foreach (var candidateUrl in await DiscoverFaviconUrlsAsync(baseUri, ct))
            {
                var img = await DownloadAndCacheImageAsync(candidateUrl, diskFile, ct);
                if (img != null)
                {
                    return img;
                }
            }
        }

        return null;
    }

    private static async Task<List<string>> DiscoverFaviconUrlsAsync(Uri baseUri, CancellationToken ct)
    {
        var urls = new List<(int Priority, string Url)>();
        try
        {
            using var response = await Http.GetAsync(baseUri, HttpCompletionOption.ResponseContentRead, ct);
            if (response.IsSuccessStatusCode)
            {
                string html = await response.Content.ReadAsStringAsync(ct);
                if (html.Length > 200_000)
                {
                    html = html[..200_000];
                }

                foreach (Match match in LinkTagRegex.Matches(html))
                {
                    string tag = match.Value;
                    var relMatch = RelAttrRegex.Match(tag);
                    var hrefMatch = HrefAttrRegex.Match(tag);
                    if (!relMatch.Success || !hrefMatch.Success)
                    {
                        continue;
                    }

                    string rel = relMatch.Groups[1].Value;
                    string href = hrefMatch.Groups[1].Value;
                    if (!rel.Contains("icon", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!Uri.TryCreate(baseUri, href, out var resolved) ||
                        !resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int priority = rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase) ? 0
                        : href.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 1
                        : 2;
                    urls.Add((priority, resolved.AbsoluteUri));
                }
            }
        }
        catch
        {
            // Fallback para /favicon.ico abaixo.
        }

        var ordered = urls.OrderBy(x => x.Priority).Select(x => x.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Uri.TryCreate(baseUri, "/favicon.ico", out var defaultFavicon))
        {
            ordered.Add(defaultFavicon.AbsoluteUri);
        }

        return ordered;
    }

    private static async Task<ImageSource?> DownloadAndCacheImageAsync(string url, string diskFile, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxIconBytes)
            {
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0 || bytes.Length > MaxIconBytes)
            {
                return null;
            }

            var image = DecodeImageBytes(bytes);
            if (image == null)
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(DiskCacheFolder);
                await File.WriteAllBytesAsync(diskFile, bytes, ct);
            }
            catch
            {
                // Falha de escrita no cache em disco nao invalida o icone ja decodificado.
            }

            return image;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? DecodeImageBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp.PixelWidth > 0 && bmp.PixelHeight > 0 ? bmp : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCreateHttpsUri(string? rawUrl, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        if (Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        return false;
    }
}

