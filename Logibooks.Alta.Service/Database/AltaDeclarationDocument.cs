// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Logibooks.Alta.CoreApi;

namespace Logibooks.Alta.Database;

internal sealed record AltaShipment(
    string Number, DateTimeOffset? DecisionAt, IReadOnlyList<AltaPayment> Payments, decimal? WeightKg);

internal static class AltaDeclarationDocument
{
    private static readonly string[] DecisionTimeFormats =
        ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];

    static AltaDeclarationDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<IReadOnlyDictionary<Guid, AltaShipment?>> ReadAsync(Stream stream, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 32_000_000
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
        var shipments = new Dictionary<Guid, AltaShipment?>();
        if (document.Root is not { } root || root.Name.LocalName != "AltaExpressCargoDeclaration")
            return shipments;

        foreach (var shipment in Children(root, "GoodsShipment_HouseShipment"))
        {
            token.ThrowIfCancellationRequested();
            if (AltaSourceValues.ReadDeclarationReference(Value(shipment, "IndGid")) is not { } id)
                continue;

            // Two records for the same source parcel cannot establish a single payment history.
            if (!shipments.TryAdd(id, ReadShipment(shipment)))
                shipments[id] = null;
        }
        return shipments;
    }

    private static AltaShipment? ReadShipment(XElement shipment)
    {
        var number = Value(shipment, "HouseWaybillDetails_PrDocumentNumber");
        if (string.IsNullOrEmpty(number))
            return null;

        var payments = new List<AltaPayment>();
        foreach (var payment in Children(shipment, "CustomsPaymentDetails"))
        {
            var mode = Value(payment, "PaymentModeCode");
            var amountText = Value(payment, "PaymentAmount");
            if (string.IsNullOrEmpty(mode) && string.IsNullOrEmpty(amountText))
                continue;
            if (!AltaSqlSource.TryReadDecimal(amountText, out var amount) || amount < 0)
                return null;

            var kind = mode switch
            {
                "1010" => "customsFee",
                "2010" or "5010" => "dutiesAndTaxes",
                _ => null
            };
            var currency = Value(payment, "PaymentCurrencyCode") switch
            {
                "643" => "RUB",
                "978" => "EUR",
                "840" => "USD",
                { Length: 3 } alpha when alpha.All(char.IsAsciiLetterUpper) => alpha,
                _ => null
            };
            if (kind is null || currency is null)
                return null;

            payments.Add(new AltaPayment(kind, amount, currency));
        }

        var date = Value(shipment, "TaxBase_DecisionDate");
        var decisionAt = date is { Length: > 19 } && (date.EndsWith('Z') || date[^6] is '+' or '-') &&
            DateTimeOffset.TryParseExact(date, DecisionTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : (DateTimeOffset?)null;

        var weightKg = Value(shipment, "UnifiedGrossWeightQuantity_MeasureUnitQualifierCode") == "166" &&
            AltaSqlSource.TryReadDecimal(Value(shipment, "UnifiedGrossWeightQuantity_GoodsQuantity"), out var weight) &&
            weight is >= 0 and < 10_000_000 ? weight : (decimal?)null;

        return new AltaShipment(number, decisionAt,
            payments.OrderBy(payment => payment.Kind, StringComparer.Ordinal)
                .ThenBy(payment => payment.Currency, StringComparer.Ordinal).ThenBy(payment => payment.Amount).ToArray(),
            weightKg);
    }

    private static IEnumerable<XElement> Children(XElement element, string localName) =>
        element.Elements().Where(child => child.Name.LocalName == localName);

    private static string? Value(XElement element, string localName) =>
        Children(element, localName).FirstOrDefault()?.Value.Trim();
}

internal static class AltaSourceValues
{
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
}
