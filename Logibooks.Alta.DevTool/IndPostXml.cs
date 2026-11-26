// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Logibooks.Alta.DevTool;

internal sealed record IndPost(string Cmr, string Number, decimal? WeightKg, XElement Document,
    IReadOnlyList<IndPostGoods> Goods);

internal sealed record IndPostGoods(string Description, decimal Quantity, decimal TotalCost, string? TnVed);

internal static class IndPostXml
{
    private const int MaxXmlCharacters = 32_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static IReadOnlyList<IndPost> ReadExport(string path, string cmr)
    {
        using var file = File.OpenRead(path);
        if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return [ReadParcel(file, cmr, Path.GetFileName(path))];
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Supply a Core .zip archive or an AltaIndPost .xml file.");

        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var parcels = new List<IndPost>();
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(entry => entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            var parcel = ReadParcel(stream, cmr, entry.FullName);
            if (!numbers.Add(parcel.Number))
                throw new InvalidDataException($"The export contains parcel {parcel.Number} more than once.");
            parcels.Add(parcel);
        }

        return parcels.Count > 0 ? parcels : throw new InvalidDataException("The archive contains no XML documents.");
    }

    public static IndPost ReadParcel(Stream stream, string cmr, string name)
    {
        try
        {
            // Core's StringWriter declares UTF-16 even though both download paths emit UTF-8.
            using var text = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            if (text.Peek() == '\uFEFF')
                text.Read();
            using var reader = XmlReader.Create(text, ReaderSettings());
            var document = XDocument.Load(reader).Root;
            if (document?.Name != "AltaIndPost")
                throw new InvalidDataException("Expected an AltaIndPost document.");

            var number = NativeValues.Required(Value(document, "NUM"), "NUM", 30);
            if (!string.Equals(cmr, Value(document, "AVIANUM"), StringComparison.Ordinal))
                throw new InvalidDataException("AVIANUM does not match the requested CMR.");
            var weight = Value(document, "ALLWEIGHT") is { } rawWeight ? NativeValues.Weight(rawWeight) : (decimal?)null;
            var goods = document.Elements("GOODS").Select(ReadGoods).ToArray();
            if (goods.Length == 0)
                throw new InvalidDataException("Expected at least one GOODS line.");
            return new IndPost(cmr, number, weight, document, goods);
        }
        catch (Exception exception) when (exception is XmlException or DecoderFallbackException or InvalidDataException)
        {
            throw new InvalidDataException($"{name}: {exception.Message}", exception);
        }
    }

    public static bool Equivalent(IndPost left, IndPost right)
    {
        var leftDocument = new XElement(left.Document);
        var rightDocument = new XElement(right.Document);
        leftDocument.Attribute("time")?.Remove();
        rightDocument.Attribute("time")?.Remove();
        return XNode.DeepEquals(leftDocument, rightDocument);
    }

    public static byte[] BuildDeclaration(string cmr, string number, string procedure,
        IEnumerable<(Guid Id, IndPost Parcel)> parcels)
    {
        var root = new XElement("AltaExpressCargoDeclaration",
            new XElement("RegNum", number),
            new XElement("AviaNum", cmr),
            new XElement("CustomsModeCode", procedure),
            parcels.Select(parcel => new XElement("GoodsShipment_HouseShipment",
                new XElement("IndGid", parcel.Id.ToString("B")),
                new XElement("HouseWaybillDetails_PrDocumentNumber", parcel.Parcel.Number),
                parcel.Parcel.Goods.Select(goods => new XElement("GoodsItemDetails",
                    new XElement("GoodsDescription", goods.Description),
                    goods.TnVed is null ? null : new XElement("GoodsTNVEDCode", goods.TnVed),
                    new XElement("SupplementaryQuantity_GoodsQuantity", goods.Quantity),
                    new XElement("CAValueAmount", new XElement("CurrencyQuantity", goods.TotalCost)))))));
        var bytes = Serialize(root);

        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, ReaderSettings());
        while (reader.Read()) { }
        return bytes;
    }

    public static byte[] Serialize(XElement document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = Utf8,
            NewLineHandling = NewLineHandling.Entitize
        }))
        {
            document.Save(writer);
        }
        return stream.ToArray();
    }

    private static IndPostGoods ReadGoods(XElement goods)
    {
        var description = goods.Element("DESCR")?.Value;
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidDataException("A GOODS line has no DESCR.");
        var quantity = NativeValues.Number(Value(goods, "QTY"), "QTY");
        if (quantity <= 0)
            throw new InvalidDataException("QTY must be positive.");
        var cost = NativeValues.Number(Value(goods, "COST"), "COST");
        if (cost < 0)
            throw new InvalidDataException("COST must be nonnegative.");
        return new(description, quantity, cost, Value(goods, "TNVED"));
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        MaxCharactersInDocument = MaxXmlCharacters
    };

    private static string? Value(XElement parent, XName name) => parent.Element(name)?.Value.Trim() is { Length: > 0 } value
        ? value
        : null;
}

internal static class NativeValues
{
    private static readonly Encoding Cyrillic = CodePagesEncodingProvider.Instance.GetEncoding(1251,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    public static string Required(string? input, string field, int maxLength)
    {
        if (input?.Trim() is not { Length: > 0 } value || value.Any(char.IsControl))
            throw new InvalidDataException($"{field} is required and must be a single value.");
        try
        {
            if (Cyrillic.GetByteCount(value) > maxLength)
                throw new InvalidDataException($"{field} exceeds its {maxLength}-byte Alta column.");
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"{field} contains characters outside the Alta database code page.", exception);
        }
        return value;
    }

    public static decimal Number(string? value, string field) => decimal.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException($"Invalid {field}.");

    public static decimal Weight(string value)
    {
        var weight = Number(value, "weight");
        return weight is >= 0 and < 10_000_000
            ? weight
            : throw new InvalidDataException("Weight must be nonnegative and smaller than 10000000 kg.");
    }

    public static string Procedure(string value) => short.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out _)
        ? value
        : throw new InvalidDataException($"Procedure must be a number from 0 to {short.MaxValue}.");

    public static int Code(string value, string field) => int.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var code) && code is >= 1 and <= 99
        ? code
        : throw new InvalidDataException($"{field} must be a number from 1 to 99.");
}
