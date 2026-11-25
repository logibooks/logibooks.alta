// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Globalization;
using System.Text.Json;
using System.Xml;
using Microsoft.Data.SqlClient;

namespace Logibooks.Alta.DevTool;

internal static class Program
{
    private const string Usage = """
        Logibooks.Alta.DevTool
          import --cmr <cmr> <archive.zip|parcel.xml>
          declare --cmr <cmr> --dec <number> --procedure <code> [--parcel <number> ...]
          decide --cmr <cmr> --dec <number> --code <1..99> [--parcel <number>] [--weight-kg <kg>]

        Set ALTA_DEV_WRITER_CONNECTION_STRING for the marked development database.
        """;

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments is [] or ["--help"] or ["-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var message = arguments switch
            {
                ["initialize"] => await DevelopmentDatabase.InitializeAsync(reset: false),
                ["reset"] => await DevelopmentDatabase.InitializeAsync(reset: true),
                ["inspect-source"] => await DevelopmentDatabase.InspectSourceAsync(),
                ["prepare-baseline", var serverBackup] => await DevelopmentDatabase.PrepareBackupAsync(serverBackup),
                ["write-manifest", var serverBackup, var localBackup] =>
                    await DevelopmentDatabase.WriteManifestAsync(serverBackup, localBackup),
                [var command, .. var options] => await RunWriterCommandAsync(command, options),
                _ => throw new InvalidDataException(Usage)
            };
            Console.WriteLine(message);
            return 0;
        }
        catch (SqlException exception)
        {
            Console.Error.WriteLine($"SQL Server {exception.Number}: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or JsonException or XmlException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static string EnvironmentValue(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new InvalidDataException($"Set {key} before running this command.");

    private static Task<string> RunWriterCommandAsync(string command, string[] options)
    {
        var parsed = command switch
        {
            "import" => CommandOptions.Parse(options, allowFile: true, "cmr"),
            "declare" => CommandOptions.Parse(options, allowFile: false, "cmr", "dec", "procedure", "parcel"),
            "decide" => CommandOptions.Parse(options, allowFile: false, "cmr", "dec", "code", "parcel", "weight-kg"),
            _ => throw new InvalidDataException(Usage)
        };
        var cmr = NativeValues.Required(parsed.One("cmr"), "CMR", 20);
        var connection = EnvironmentValue("ALTA_DEV_WRITER_CONNECTION_STRING");
        if (command == "import")
        {
            var input = IndPostXml.ReadExport(parsed.File ?? throw new InvalidDataException("Supply a Core XML or ZIP file."), cmr);
            return DevelopmentStore.RunAsync(connection, store => store.ImportAsync(cmr, input));
        }

        var number = NativeValues.Required(parsed.One("dec"), "DEC number", 25);
        if (command == "declare")
        {
            var procedure = NativeValues.Code(parsed.One("procedure")!, "Procedure").ToString("D2", CultureInfo.InvariantCulture);
            var parcels = parsed.Many("parcel").Select(value => NativeValues.Required(value, "Parcel number", 30)).ToArray();
            if (parcels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parcels.Length)
                throw new InvalidDataException("Specify each parcel only once.");
            return DevelopmentStore.RunAsync(connection, store => store.DeclareAsync(cmr, number, procedure, parcels));
        }

        var code = NativeValues.Code(parsed.One("code")!, "Decision code");
        var parcel = parsed.One("parcel", required: false) is { } selection
            ? NativeValues.Required(selection, "Parcel number", 30) : null;
        var weight = parsed.One("weight-kg", required: false) is { } rawWeight ? NativeValues.Weight(rawWeight) : (decimal?)null;
        if (weight.HasValue && parcel is null)
            throw new InvalidDataException("Select one --parcel when overriding its weight.");
        return DevelopmentStore.RunAsync(connection, store => store.DecideAsync(cmr, number, code, parcel, weight));
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

        public string? File { get; private set; }

        public static CommandOptions Parse(string[] arguments, bool allowFile, params string[] allowed)
        {
            var options = new CommandOptions();
            for (var index = 0; index < arguments.Length; index++)
            {
                var key = arguments[index];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    if (!allowFile || options.File is not null)
                        throw new InvalidDataException($"Unexpected argument {key}.");
                    options.File = key;
                    continue;
                }

                if (!allowed.Contains(key[2..], StringComparer.Ordinal) || index + 1 == arguments.Length ||
                    arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidDataException($"Invalid option {key} or missing value.");
                if (!options._values.TryGetValue(key[2..], out var values))
                    options._values.Add(key[2..], values = []);
                values.Add(arguments[++index]);
            }
            return options;
        }

        public IReadOnlyList<string> Many(string key) => _values.GetValueOrDefault(key) ?? [];

        public string? One(string key, bool required = true) => Many(key) switch
        {
            [var value] => value,
            [] when !required => null,
            _ => throw new InvalidDataException($"Supply exactly one --{key} value.")
        };
    }
}
