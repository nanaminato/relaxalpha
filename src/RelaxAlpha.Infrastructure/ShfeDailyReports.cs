using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Infrastructure;

public sealed class ExchangeDailyOptions
{
    public bool Enabled { get; init; }
    public string? OfflineDirectory { get; init; }
    public string? SeedDirectory { get; init; }
    public DateOnly? CalendarThrough { get; init; }
    public DateOnly[] AdditionalClosures { get; init; } = [];
}

public sealed class ShfeDailyReports(HttpClient http, ExchangeDailyOptions options)
{
    public const string Source = "上期所官网日行情";
    public const string BaseUrl = "https://www.shfe.com.cn";
    public const int MaxFileBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> IneProducts = new(StringComparer.OrdinalIgnoreCase) { "bc", "sc", "lu", "nr" };

    internal sealed record Report(string Directory, string Prefix, string Rows, string FilePrefix)
    {
        public string Path(DateOnly day) => $"/data/{Directory}/{Prefix}{DayText(day)}.dat";
        public string OfflineName(DateOnly day) => $"{FilePrefix}-{DayText(day)}.dat";
    }

    internal static readonly Report Futures = new("tradedata/future/dailydata", "kx", "o_curinstrument", "futures");
    internal static readonly Report Options = new("tradedata/option/dailydata", "kx", "o_curinstrument", "options");
    internal static readonly Report Contracts = new("busiparamdata/option", "ContractBaseInfo", "OptionContractBaseInfo", "contracts");
    internal static readonly Report Fees = new("busiparamdata/option", "Settlement", "OptionSettlement", "fees");

    internal async Task<byte[]> ReadAsync(Report report, DateOnly day, CancellationToken ct)
    {
        byte[] data;
        var offline = !string.IsNullOrWhiteSpace(options.OfflineDirectory);
        var directory = offline ? options.OfflineDirectory : options.SeedDirectory;
        var name = report.OfflineName(day);
        var file = string.IsNullOrWhiteSpace(directory) ? null : new FileInfo(Path.Combine(directory, name));
        if (file is { Exists: true })
        {
            if (file.Length > MaxFileBytes)
                throw new InvalidDataException($"本地文件 {name} 超过大小限制。");
            data = await File.ReadAllBytesAsync(file.FullName, ct);
        }
        else if (offline)
        {
            throw new InvalidDataException($"缺少离线文件 {name}。");
        }
        else
        {
            using var response = await http.GetAsync(BaseUrl + report.Path(day), ct);
            response.EnsureSuccessStatusCode();
            data = await response.Content.ReadAsByteArrayAsync(ct);
        }

        using var document = Open(data, report, day);
        return data;
    }

    internal static ImportBatch BuildBatch(OfficialDailyImport files, DateTime now)
    {
        var day = files.TradingDate;
        using var futures = Open(files.Futures!, Futures, day);
        using var options = Open(files.Options!, Options, day);
        using var contracts = Open(files.Contracts!, Contracts, day);
        using var fees = Open(files.Fees!, Fees, day);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in new[] { files.Futures!, files.Options!, files.Contracts!, files.Fees! })
            hash.AppendData(SHA256.HashData(file));

        var observed = day.ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc);
        var batch = new ImportBatch
        {
            Kind = SourceKind.ExchangePublic, Source = Source, TradingDate = day, ObservedAtUtc = observed,
            ImportedAtUtc = now, ContentHash = Convert.ToHexString(hash.GetHashAndReset())
        };
        var futuresBySymbol = futures.RootElement.GetProperty(Futures.Rows).EnumerateArray()
            .Select(row => (Product: Text(row, "PRODUCTID"), Month: Text(row, "DELIVERYMONTH"), Price: Number(row, "SETTLEMENTPRICE")))
            .Where(x => x.Product.EndsWith("_f", StringComparison.OrdinalIgnoreCase) &&
                x.Month.Length == 4 && x.Month.All(char.IsAsciiDigit) && x.Price is > 0)
            .ToDictionary(x => x.Product[..^2] + x.Month, x => x.Price!.Value, StringComparer.OrdinalIgnoreCase);
        var contractsBySymbol = Index(contracts, Contracts, day);
        var feesBySymbol = Index(fees, Fees, day);

        foreach (var row in options.RootElement.GetProperty(Options.Rows).EnumerateArray())
        {
            var symbol = Text(row, "INSTRUMENTID");
            var underlying = Text(row, "UNDERLYINGINSTRID");
            var product = Text(row, "PRODUCTGROUPID");
            var type = Text(row, "OPTIONSTYPE");
            if (type is not ("1" or "2") || !futuresBySymbol.TryGetValue(underlying, out var futuresPrice) ||
                !contractsBySymbol.TryGetValue(symbol, out var contract) || !feesBySymbol.TryGetValue(symbol, out var fee) ||
                !DateOnly.TryParseExact(Text(contract, "EXPIREDATE"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) ||
                expiry <= day || product.Length == 0 || !Text(contract, "COMMODITYID").Equals(product, StringComparison.OrdinalIgnoreCase))
                continue;

            var strike = Number(row, "STRIKEPRICE");
            var settlement = Number(row, "SETTLEMENTPRICE");
            var multiplier = Number(contract, "TRADEUNIT");
            var tradeFee = Number(fee, "TRADEFEEUNIT");
            var exerciseFee = Number(fee, "STRIKEFEEUNIT");
            var volume = Count(row, "VOLUME");
            var interest = Count(row, "OPENINTEREST");
            if (strike is not > 0 || settlement is not > 0 || multiplier is not > 0 ||
                tradeFee is not >= 0 || exerciseFee is not >= 0 || volume is null || interest is null ||
                Number(fee, "TRADEFEERATIO") != 0 || Number(fee, "STRIKEFEERATIO") != 0)
                continue;

            batch.Quotes.Add(new OptionQuote
            {
                Exchange = IneProducts.Contains(product) ? "INE" : "SHFE",
                Symbol = symbol.ToUpperInvariant(), Underlying = underlying.ToUpperInvariant(),
                ExpiryUtc = expiry.ToDateTime(new TimeOnly(7, 0), DateTimeKind.Utc),
                Side = type == "1" ? OptionSide.Call : OptionSide.Put,
                Strike = strike.Value, FuturesPrice = futuresPrice, Multiplier = multiplier.Value,
                OpenFee = tradeFee.Value, CloseFee = tradeFee.Value, ExerciseFee = exerciseFee.Value,
                SettlementPrice = settlement, Volume = volume.Value, OpenInterest = interest.Value, Quality = QuoteQuality.DailySettlement
            });
        }

        if (batch.Quotes.Count == 0)
            throw new InvalidDataException("官方日文件没有可配对的有效期权、标的、合约和费用记录。");
        return batch;
    }

    private static Dictionary<string, JsonElement> Index(JsonDocument document, Report report, DateOnly day) =>
        document.RootElement.GetProperty(report.Rows).EnumerateArray()
            .Where(row => Text(row, "TRADINGDAY") == DayText(day) && Text(row, "INSTRUMENTID").Length > 0)
            .ToDictionary(row => Text(row, "INSTRUMENTID"), StringComparer.OrdinalIgnoreCase);

    private static JsonDocument Open(byte[] data, Report report, DateOnly day)
    {
        JsonDocument document;
        try
        {
            var json = data.AsMemory();
            if (json.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"官方文件 {report.OfflineName(day)} 不是有效的 JSON。", e);
        }

        var root = document.RootElement;
        if (Text(root, "report_date") == DayText(day) && root.TryGetProperty(report.Rows, out var rows) &&
            rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
            return document;

        document.Dispose();
        throw new InvalidDataException($"官方文件 {report.OfflineName(day)} 的日期不符或没有数据。");
    }

    private static string DayText(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Text(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()?.Trim() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                _ => ""
            }
            : "";

    private static decimal? Number(JsonElement row, string key) =>
        decimal.TryParse(Text(row, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
        value <= 1_000_000_000 ? value : null;

    private static int? Count(JsonElement row, string key) =>
        int.TryParse(Text(row, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
