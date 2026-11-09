using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using RelaxAlpha.Application;
using RelaxAlpha.Domain;

namespace RelaxAlpha.Infrastructure;

public sealed class CsvMarketDataSource(TimeProvider time) : IMarketDataSource
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxRows = 20_000;
    public const string Header = "exchange,symbol,underlying,expiry,side,strike,futures_price,multiplier,open_fee,close_fee,exercise_fee,bid,ask,volume,open_interest";
    private static readonly string[] Exchanges = ["SHFE", "INE", "DCE", "CZCE", "GFEX"];

    public static bool TryTimestamp(string? value, out DateTime utc)
    {
        utc = default;
        if (value is null || !Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$") ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            return false;
        utc = stamp.UtcDateTime;
        return true;
    }

    public async Task<ImportBatch> ReadAsync(Stream input, SourceInfo source, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (source.Source.Trim().Length is < 1 or > 160 || !Enum.IsDefined(source.Kind) ||
            source.ObservedAtUtc.Kind != DateTimeKind.Utc || source.ObservedAtUtc > now)
            throw new InvalidDataException("请填写来源及带时区的行情时间，行情时间不得晚于当前时间。");

        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > MaxBytes) throw new InvalidDataException("CSV 超过 4 MiB。");
            bytes.Write(buffer, 0, read);
        }

        var payload = bytes.ToArray();
        var batch = new ImportBatch
        {
            Source = source.Source.Trim(), Kind = source.Kind, ObservedAtUtc = source.ObservedAtUtc,
            ImportedAtUtc = now, ContentHash = Convert.ToHexString(SHA256.HashData(payload))
        };

        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload).TrimStart('\uFEFF');
            using var parser = new TextFieldParser(new StringReader(text));
            parser.SetDelimiters(",");
            var headers = parser.ReadFields();
            var required = Header.Split(',');
            if (headers is null || headers.Length != required.Length ||
                !required.All(h => headers.Contains(h, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("CSV 表头不符合规范，请使用文档提供的字段。");

            var columns = required.Select(h => Array.FindIndex(headers, x => x.Equals(h, StringComparison.OrdinalIgnoreCase))).ToArray();
            var symbols = new HashSet<(string, string)>();
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batch.Quotes.Count >= MaxRows) throw new InvalidDataException("每批最多导入 20,000 条行情。");
                var row = parser.LineNumber;
                try
                {
                    var fields = parser.ReadFields()!;
                    if (fields.Length != required.Length) throw new FormatException("字段数量不正确。");
                    var quote = Parse(columns.Select(i => fields[i]).ToArray(), source.ObservedAtUtc);
                    if (source.Kind != SourceKind.Simulated && quote.Symbol.StartsWith("DEMO-", StringComparison.Ordinal))
                        throw new FormatException("DEMO- 合约必须归入模拟数据。");
                    if (!symbols.Add((quote.Exchange, quote.Symbol))) throw new FormatException("批次内合约重复。");
                    batch.Quotes.Add(quote);
                }
                catch (FormatException error)
                {
                    throw new InvalidDataException($"CSV 第 {row} 行：{error.Message}", error);
                }
            }
        }
        catch (MalformedLineException error)
        {
            throw new InvalidDataException($"CSV 第 {error.LineNumber} 行格式错误。", error);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("CSV 必须使用 UTF-8。", error);
        }

        if (batch.Quotes.Count == 0) throw new InvalidDataException("CSV 没有行情记录。");
        return batch;
    }

    private static OptionQuote Parse(string[] f, DateTime observed)
    {
        string Code(int i)
        {
            if (!Regex.IsMatch(f[i], "^[A-Za-z0-9_.-]{1,48}$")) throw new FormatException("合约代码不合法。");
            return f[i].ToUpperInvariant();
        }

        decimal Number(int i, bool positive = false)
        {
            if (!decimal.TryParse(f[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
                value > 1_000_000_000 || (positive && value <= 0))
                throw new FormatException($"数值不合法：{Header.Split(',')[i]}。");
            return value;
        }

        int Count(int i) => int.TryParse(f[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new FormatException("成交量和持仓量必须为非负整数。");

        var exchange = Code(0);
        if (!Exchanges.Contains(exchange)) throw new FormatException("未知商品交易所。");
        if (!TryTimestamp(f[3], out var expiry) || expiry <= observed)
            throw new FormatException("到期时间必须带时区且晚于行情时间。");
        var side = f[4].ToUpperInvariant() switch
        {
            "C" or "CALL" => OptionSide.Call,
            "P" or "PUT" => OptionSide.Put,
            _ => throw new FormatException("方向必须为 C 或 P。")
        };

        decimal? bid = f[11].Length == 0 ? null : Number(11);
        decimal? ask = f[12].Length == 0 ? null : Number(12);
        var quality = bid > ask ? QuoteQuality.Crossed :
            bid > 0 && ask > 0 ? QuoteQuality.TwoSided : QuoteQuality.Indicative;
        return new OptionQuote
        {
            Exchange = exchange, Symbol = Code(1), Underlying = Code(2), ExpiryUtc = expiry, Side = side,
            Strike = Number(5, true), FuturesPrice = Number(6, true), Multiplier = Number(7, true),
            OpenFee = Number(8), CloseFee = Number(9), ExerciseFee = Number(10),
            Bid = bid, Ask = ask, Volume = Count(13), OpenInterest = Count(14), Quality = quality
        };
    }
}
