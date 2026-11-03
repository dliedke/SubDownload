using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubDownload;

/// <summary>
/// Configuracao local (chaves de API), lida de SubDownload.config.json na pasta do
/// executavel. Esse arquivo nunca vai pro git (ver SubDownload.config.example.json). A
/// variavel de ambiente SUBDL_API_KEY, se definida, tem prioridade sobre o arquivo.
/// </summary>
/// <param name="SubDlApiKey">Chave do SubDL, ou null se nao houver (vazia conta como sem chave).</param>
/// <param name="SubDlDisabled">
/// O usuario escolheu nao usar o SubDL ("subdlDisabled": true) — nao pergunta mais a chave.
/// </param>
internal sealed record AppConfig(string? SubDlApiKey, bool SubDlDisabled)
{
    public const string FileName = "SubDownload.config.json";
    private const string SubDlApiKeyProperty = "subdlApiKey";
    private const string SubDlDisabledProperty = "subdlDisabled";
    private const string SubDlApiKeyEnvVar = "SUBDL_API_KEY";

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static AppConfig Load()
    {
        var json = ReadJson();
        var fileKey = json?[SubDlApiKeyProperty] is JsonValue k && k.TryGetValue<string>(out var s) ? s : null;
        var disabled = json?[SubDlDisabledProperty] is JsonValue d && d.TryGetValue<bool>(out var b) && b;

        var envKey = Environment.GetEnvironmentVariable(SubDlApiKeyEnvVar);
        var key = !string.IsNullOrWhiteSpace(envKey) ? envKey : fileKey;
        return new AppConfig(string.IsNullOrWhiteSpace(key) ? null : key.Trim(), disabled);
    }

    public static void SaveSubDlApiKey(string apiKey) => Update(json =>
    {
        json[SubDlApiKeyProperty] = apiKey;
        json.Remove(SubDlDisabledProperty);
    });

    public static void DisableSubDl() => Update(json => json[SubDlDisabledProperty] = true);

    // Altera so o que precisa, mantendo as demais configuracoes que ja estiverem no arquivo.
    private static void Update(Action<JsonObject> change)
    {
        var json = ReadJson() ?? new JsonObject();
        change(json);
        File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject? ReadJson()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath), documentOptions: ReadOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[aviso] {FileName} invalido, ignorando: {ex.Message}");
            return null;
        }
    }
}
