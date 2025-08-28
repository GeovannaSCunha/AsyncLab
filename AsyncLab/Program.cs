using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

// =================== Configuração ===================
// Iterações elevadas deixam o trabalho realmente pesado (CPU-bound).
const int PBKDF2_ITERATIONS = 50_000;
const int HASH_BYTES = 32; // 32 = 256 bits
const string CSV_URL = "https://www.gov.br/receitafederal/dados/municipios.csv";
const string OUT_DIR_NAME = "mun_hash_por_uf";

string FormatTempo(long ms)
{
    var ts = TimeSpan.FromMilliseconds(ms);
    return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
}

// =================== Download do CSV (I/O-bound) ===================
Console.WriteLine("Baixando CSV de municípios...");
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
byte[] csvBytes = await http.GetByteArrayAsync(CSV_URL);

// O arquivo da RFB costuma estar em ISO-8859-1/Latin1; se vier em UTF-8, também funciona.
string csvText;
try
{
    csvText = Encoding.Latin1.GetString(csvBytes);
    if (csvText.Contains("�")) // fallback simples
        csvText = Encoding.UTF8.GetString(csvBytes);
}
catch
{
    csvText = Encoding.UTF8.GetString(csvBytes);
}

// =================== Parse do CSV ===================
Console.WriteLine("Processando CSV...");
var linhas = csvText
    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
    .ToList();

// Cabeçalho esperado: TOM;IBGE;Nome(TOM);Nome(IBGE);UF
bool temCabecalho = linhas.Count > 0 && linhas[0].Contains(";");
if (temCabecalho) linhas = linhas.Skip(1).ToList();

List<Municipio> municipios = new();
foreach (var ln in linhas)
{
    var parts = ln.Split(';');
    if (parts.Length < 5) continue;
    municipios.Add(new Municipio
    {
        Tom = parts[0].Trim(),
        Ibge = parts[1].Trim(),
        NomeTom = parts[2].Trim(),
        NomeIbge = parts[3].Trim(),
        Uf = parts[4].Trim().ToUpperInvariant()
    });
}

// =================== Saída ===================
var outRoot = Path.Combine(Environment.CurrentDirectory, OUT_DIR_NAME);
Directory.CreateDirectory(outRoot);

var ufsOrdenadas = municipios
    .Select(m => m.Uf)
    .Where(u => !string.IsNullOrWhiteSpace(u))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(u => u)
    .ToList();

Console.WriteLine($"UFs encontradas: {ufsOrdenadas.Count}");
Console.WriteLine($"Pasta de saída: {outRoot}");
Console.WriteLine();

var swTotal = Stopwatch.StartNew();

// =================== Loop por UF ===================
foreach (var uf in ufsOrdenadas)
{
    var swUf = Stopwatch.StartNew();
    var doUf = municipios.Where(m => string.Equals(m.Uf, uf, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"UF {uf}: {doUf.Count} municípios");

    // Containers thread-safe para escrita em paralelo
    var linhasOut = new ConcurrentBag<string>();
    var jsonOut = new ConcurrentBag<object>();

    // Header CSV de saída
    linhasOut.Add("TOM;IBGE;Nome(TOM);Nome(IBGE);UF;HASH_HEX");

    // =================== Parte CPU-bound paralelizada ===================
    await Task.Run(() =>
    {
        Parallel.ForEach(
            doUf,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            m =>
            {
                string password = m.ToConcatenatedString();
                byte[] salt = Util.BuildSalt(m.Ibge);
                string hashHex = Util.DeriveHashHex(password, salt, PBKDF2_ITERATIONS, HASH_BYTES);

                linhasOut.Add($"{m.Tom};{m.Ibge};{Util.San(m.NomeTom)};{Util.San(m.NomeIbge)};{m.Uf};{hashHex}");
                jsonOut.Add(new
                {
                    m.Tom,
                    m.Ibge,
                    m.NomeTom,
                    m.NomeIbge,
                    m.Uf,
                    HashHex = hashHex
                });
            });
    });

    // =================== Escrita assíncrona em disco (I/O-bound) ===================
    string csvPath = Path.Combine(outRoot, $"municipios_hash_{uf}.csv");
    string jsonPath = Path.Combine(outRoot, $"municipios_hash_{uf}.json");

    await File.WriteAllLinesAsync(csvPath, linhasOut, Encoding.UTF8);

    var json = JsonSerializer.Serialize(jsonOut, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8);

    swUf.Stop();
    Console.WriteLine($"UF {uf} concluída em {FormatTempo(swUf.ElapsedMilliseconds)}");
}

swTotal.Stop();
Console.WriteLine();
Console.WriteLine("===== RESUMO =====");
Console.WriteLine($"UFs geradas: {ufsOrdenadas.Count}");
Console.WriteLine($"Pasta de saída: {outRoot}");
Console.WriteLine($"Tempo total: {FormatTempo(swTotal.ElapsedMilliseconds)} ({swTotal.Elapsed})");
