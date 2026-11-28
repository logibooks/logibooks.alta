// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Logibooks.Alta.CoreApi;

namespace Logibooks.Alta.Database;

internal sealed record AltaShipment(
    string? Number, DateTimeOffset? DecisionAt, IReadOnlyList<AltaPayment> Payments, decimal? WeightKg,
    IReadOnlyList<AltaGoods> Goods);

internal readonly record struct AltaShipmentRead(AltaShipment? Shipment, string? Failure = null);

internal sealed record AltaDeclarationDetails(IReadOnlyDictionary<Guid, AltaShipmentRead> Shipments, string? Failure = null)
{
    public AltaDeclarationDetails(string failure) : this(ReadOnlyDictionary<Guid, AltaShipmentRead>.Empty, failure)
    {
    }
}

internal static class AltaDeclarationDocument
{
    private static readonly string[] DecisionTimeFormats =
        ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    static AltaDeclarationDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<AltaDeclarationDetails> ReadAsync(Stream stream,
        IReadOnlySet<Guid> parcelIds, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 32_000_000
        });

        XDocument document;
        try
        {
            document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
        }
        catch (XmlException exception)
        {
            return new AltaDeclarationDetails(
                $"Could not parse DEC XML in Docs.BLOB (line {exception.LineNumber}, position {exception.LinePosition})");
        }
        if (document.Root is not { } root || root.Name.LocalName != "AltaExpressCargoDeclaration")
            return new AltaDeclarationDetails("DEC XML in Docs.BLOB must have an AltaExpressCargoDeclaration root element");

        var shipments = new Dictionary<Guid, AltaShipmentRead>();
        foreach (var shipment in Children(root, "GoodsShipment_HouseShipment"))
        {
            token.ThrowIfCancellationRequested();
            if (AltaSourceValues.ReadDeclarationReference(Value(shipment, "IndGid")) is not { } id || !parcelIds.Contains(id))
                continue;

            // Duplicate shipments for the same IndPost document make the assignment ambiguous.
            if (!shipments.TryAdd(id, ReadShipment(shipment)))
                shipments[id] = new(null, "Multiple GoodsShipment_HouseShipment elements have the same IndGid");
        }
        return new AltaDeclarationDetails(shipments);
    }

    private static AltaShipmentRead ReadShipment(XElement shipment)
    {
        var payments = new List<AltaPayment>();
        var paymentIndex = 0;
        foreach (var payment in Children(shipment, "CustomsPaymentDetails"))
        {
            paymentIndex++;
            var mode = Value(payment, "PaymentModeCode");
            var amountText = Value(payment, "PaymentAmount");
            if (string.IsNullOrEmpty(mode) && string.IsNullOrEmpty(amountText))
                continue;
            if (string.IsNullOrEmpty(amountText))
                return new(null, $"Missing CustomsPaymentDetails #{paymentIndex}/PaymentAmount");
            if (!AltaSourceValues.TryReadDecimal(amountText, out var amount) || amount < 0)
                return new(null, $"Invalid CustomsPaymentDetails #{paymentIndex}/PaymentAmount (expected a nonnegative decimal)");

            var kind = mode switch
            {
                "1010" => "customsFee",
                "2010" or "5010" => "dutiesAndTaxes",
                _ => null
            };
            if (kind is null)
                return new(null, string.IsNullOrEmpty(mode)
                    ? $"Missing CustomsPaymentDetails #{paymentIndex}/PaymentModeCode"
                    : $"Invalid CustomsPaymentDetails #{paymentIndex}/PaymentModeCode (expected 1010, 2010, or 5010)");

            var currencyCode = Value(payment, "PaymentCurrencyCode");
            var currency = currencyCode switch
            {
                "643" => "RUB",
                "978" => "EUR",
                "840" => "USD",
                { Length: 3 } alpha when alpha.All(char.IsAsciiLetterUpper) => alpha,
                _ => null
            };
            if (currency is null)
                return new(null, string.IsNullOrEmpty(currencyCode)
                    ? $"Missing CustomsPaymentDetails #{paymentIndex}/PaymentCurrencyCode"
                    : $"Invalid CustomsPaymentDetails #{paymentIndex}/PaymentCurrencyCode (expected 643, 978, 840, or three uppercase letters)");

            payments.Add(new AltaPayment(kind, amount, currency));
        }
        var date = Value(shipment, "TaxBase_DecisionDate");
        var decisionAt = date is { Length: > 19 } && (date.EndsWith('Z') || date[^6] is '+' or '-') &&
            DateTimeOffset.TryParseExact(date, DecisionTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : (DateTimeOffset?)null;
        var weightKg = Value(shipment, "UnifiedGrossWeightQuantity_MeasureUnitQualifierCode") == "166" &&
            AltaSourceValues.TryReadDecimal(Value(shipment, "UnifiedGrossWeightQuantity_GoodsQuantity"), out var weight) &&
            weight is >= 0 and < 10_000_000 ? weight : (decimal?)null;
        var goods = ReadGoods(shipment, out var failure);
        if (goods is null)
            return new(null, failure);

        return new(new AltaShipment(Value(shipment, "HouseWaybillDetails_PrDocumentNumber"), decisionAt,
            payments.OrderBy(payment => payment.Kind, StringComparer.Ordinal)
                .ThenBy(payment => payment.Currency, StringComparer.Ordinal).ThenBy(payment => payment.Amount).ToArray(),
            weightKg, goods));
    }

    private static IReadOnlyList<AltaGoods>? ReadGoods(XElement shipment, out string? failure)
    {
        var goods = new List<AltaGoods>();
        foreach (var item in Children(shipment, "GoodsItemDetails"))
        {
            var itemIndex = goods.Count + 1;
            var description = Children(item, "GoodsDescription").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(description))
            {
                failure = $"Missing GoodsItemDetails #{itemIndex}/GoodsDescription";
                return null;
            }

            // The first CAValueAmount carries the total in the invoice currency.
            var declaredValue = Children(item, "CAValueAmount").FirstOrDefault();
            if (declaredValue is null)
            {
                failure = $"Missing GoodsItemDetails #{itemIndex}/CAValueAmount";
                return null;
            }

            // Core validates business rules for the parsed values and records any rejection.
            var quantityText = Value(item, "SupplementaryQuantity_GoodsQuantity");
            if (!AltaSourceValues.TryReadDecimal(quantityText, out var quantity))
            {
                failure = string.IsNullOrEmpty(quantityText)
                    ? $"Missing GoodsItemDetails #{itemIndex}/SupplementaryQuantity_GoodsQuantity"
                    : $"Invalid GoodsItemDetails #{itemIndex}/SupplementaryQuantity_GoodsQuantity (expected a decimal)";
                return null;
            }
            var totalText = Value(declaredValue, "CurrencyQuantity");
            if (!AltaSourceValues.TryReadDecimal(totalText, out var total))
            {
                failure = string.IsNullOrEmpty(totalText)
                    ? $"Missing GoodsItemDetails #{itemIndex}/CAValueAmount/CurrencyQuantity"
                    : $"Invalid GoodsItemDetails #{itemIndex}/CAValueAmount/CurrencyQuantity (expected a decimal)";
                return null;
            }

            var tnVed = Value(item, "GoodsTNVEDCode");
            goods.Add(new AltaGoods(description, string.IsNullOrEmpty(tnVed) ? null : tnVed, quantity, total));
        }
        failure = goods.Count == 0 ? "Missing GoodsShipment_HouseShipment/GoodsItemDetails" : null;
        return goods.Count == 0 ? null : goods;
    }

    private static IEnumerable<XElement> Children(XElement element, string localName) =>
        element.Elements().Where(child => child.Name.LocalName == localName);

    private static string? Value(XElement element, string localName) =>
        Children(element, localName).FirstOrDefault()?.Value.Trim();
}

internal static class AltaSourceValues
{
    public static string? ReadDeclarationNumber(string? listRegNum, string? documentRegNum)
    {
        var number = string.IsNullOrWhiteSpace(listRegNum) ? documentRegNum : listRegNum;
        return string.IsNullOrWhiteSpace(number) ? null : number.Trim();
    }

    public static Guid? ReadDeclarationReference(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        // The restored source uses {guid}; some imports decorate the same GUID with a reference prefix.
        if (value.StartsWith(@"{\<", StringComparison.Ordinal) && value.EndsWith('}'))
            value = value[3..^1];
        else if (value.StartsWith("{<", StringComparison.Ordinal) && value.EndsWith('}'))
            value = value[2..^1];
        return (Guid.TryParseExact(value, "B", out var id) || Guid.TryParseExact(value, "D", out id)) &&
               id != Guid.Empty ? id : null;
    }

    public static bool TryReadDecimal(string? source, out decimal value)
    {
        var text = source?.Trim() ?? "";
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value))
            return false;

        // Decimal.TryParse can round an otherwise valid value with more precision than decimal supports.
        var unsigned = text.TrimStart('+', '-');
        var dot = unsigned.IndexOf('.');
        var whole = (dot < 0 ? unsigned : unsigned[..dot]).TrimStart('0');
        var fraction = dot < 0 ? "" : unsigned[(dot + 1)..].TrimEnd('0');
        var canonical = whole.Length == 0 ? "0" : whole;
        if (fraction.Length != 0)
            canonical += "." + fraction;
        if (text.StartsWith('-') && canonical != "0")
            canonical = "-" + canonical;
        return canonical == value.ToString("0.############################", CultureInfo.InvariantCulture);
    }
}
