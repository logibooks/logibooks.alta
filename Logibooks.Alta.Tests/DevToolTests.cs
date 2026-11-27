// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Logibooks.Alta.Database;
using Logibooks.Alta.DevTool;

namespace Logibooks.Alta.Tests;

[TestFixture]
public sealed class DevToolTests
{
    [TestCase("0")]
    [TestCase("1")]
    [TestCase("01")]
    [TestCase("100")]
    [TestCase("32767")]
    public void ProcedurePreservesValidDigits(string value) =>
        Assert.That(NativeValues.Procedure(value), Is.EqualTo(value));

    [TestCase("")]
    [TestCase("-1")]
    [TestCase("+1")]
    [TestCase("1 ")]
    [TestCase("32768")]
    public void ProcedureRejectsInvalidValues(string value) =>
        Assert.That(() => NativeValues.Procedure(value), Throws.TypeOf<InvalidDataException>());

    [TestCase(false, "3")]
    [TestCase(true, "3")]
    [TestCase(false, "1.5")]
    public async Task CoreUtf8ExportProducesReadableIndPostAndOrderedDeclaration(bool withBom, string quantity)
    {
        using var source = new MemoryStream(CoreBytes(withBom, "00042", "CMR-42", "2026-11-25", quantity: quantity));
        var parcel = IndPostXml.ReadParcel(source, "CMR-42", "IndPost_00042.xml");
        using var stored = new MemoryStream(IndPostXml.Serialize(parcel.Document));
        var xml = XDocument.Load(stored);
        Assert.That(xml.Declaration?.Encoding, Is.EqualTo("utf-8"));
        Assert.That(xml.Root!.Elements("GOODS").First().Element("DESCR")!.Value, Is.EqualTo("Товар\rШК"));

        var parcelId = Guid.NewGuid();
        using var declaration = new MemoryStream(IndPostXml.BuildDeclaration("CMR-42", "10000000/251126/0000042", "40",
            [(parcelId, parcel)]));
        var shipments = await AltaDeclarationDocument.ReadAsync(declaration, new HashSet<Guid> { parcelId },
            CancellationToken.None);
        var shipment = shipments.Shipments[parcelId].Shipment!;
        Assert.Multiple(() =>
        {
            Assert.That(parcel.WeightKg, Is.EqualTo(1.25m));
            Assert.That(shipment.Number, Is.EqualTo("00042"));
            Assert.That(shipment.WeightKg, Is.Null);
            Assert.That(shipment.Goods!.Select(goods => goods.Description), Is.EqualTo(new[] { "Товар\rШК", "Другой товар" }));
            Assert.That(shipment.Goods!.Select(goods => goods.Quantity),
                Is.EqualTo(new[] { decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), 1m }));
            Assert.That(shipment.Goods!.Select(goods => goods.TotalCost), Is.EqualTo(new[] { 7.35m, 1m }));
            Assert.That(shipment.Goods!.Select(goods => goods.TnVed), Is.EqualTo(new[] { "1234567890", null }));
        });
    }

    [Test]
    public void RepeatImportIgnoresExportTimeAndDetectsChangedLineTotal()
    {
        using var original = new MemoryStream(CoreBytes(false, "00042", "CMR-42", "2026-11-25"));
        using var reexport = new MemoryStream(CoreBytes(true, "00042", "CMR-42", "2026-11-26"));
        using var changed = new MemoryStream(CoreBytes(false, "00042", "CMR-42", "2026-11-26", "735.00"));
        var first = IndPostXml.ReadParcel(original, "CMR-42", "original");
        Assert.That(IndPostXml.Equivalent(first, IndPostXml.ReadParcel(reexport, "CMR-42", "reexport")), Is.True);
        Assert.That(IndPostXml.Equivalent(first, IndPostXml.ReadParcel(changed, "CMR-42", "changed")), Is.False);
    }

    [TestCase("00043", "CMR-42", true)]
    [TestCase("00042", "CMR-42", false)]
    [TestCase("00043", "CMR-OTHER", false)]
    public void ReadsNestedArchiveAndRejectsDuplicateOrMixedCmr(string secondNumber, string secondCmr, bool valid)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alta-{Guid.NewGuid():N}.zip");
        try
        {
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var first = archive.CreateEntry("no-payment-73860/IndPost_00042.xml");
                using (var entry = first.Open())
                    entry.Write(CoreBytes(true, "00042", "CMR-42", "2026-11-25"));
                var second = archive.CreateEntry("no-payment-73860/second.xml");
                using (var entry = second.Open())
                    entry.Write(CoreBytes(false, secondNumber, secondCmr, "2026-11-25"));
            }
            if (valid)
                Assert.That(IndPostXml.ReadExport(path, "CMR-42").Select(parcel => parcel.Number),
                    Is.EqualTo(new[] { "00042", "00043" }));
            else
                Assert.That(() => IndPostXml.ReadExport(path, "CMR-42"), Throws.TypeOf<InvalidDataException>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CoreBytes(bool withBom, string number, string cmr, string time, string cost = "7.35",
        string quantity = "3")
    {
        var encoding = new UTF8Encoding(withBom);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes($"""
            <?xml version="1.0" encoding="utf-16"?>
            <AltaIndPost time="{time}">
              <AVIANUM>{cmr}</AVIANUM><NUM>{number}</NUM><ALLWEIGHT>1.250</ALLWEIGHT>
              <GOODS><DESCR>Товар&#xD;ШК</DESCR><QTY>{quantity}</QTY><COST>{cost}</COST><TNVED>1234567890</TNVED></GOODS>
              <GOODS><DESCR>Другой товар</DESCR><QTY>1</QTY><COST>1.00</COST></GOODS>
            </AltaIndPost>
            """)];
    }
}
