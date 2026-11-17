// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Text;

namespace Logibooks.Alta.Monitoring;

internal interface IAltaConsoleTerminal
{
    Task<string?> ReadLineAsync(string prompt, CancellationToken token);
    void WriteLine(string text);
}

internal sealed class AltaConsoleTerminal : IAltaConsoleTerminal
{
    public void WriteLine(string text) => Console.WriteLine(text);

    public async Task<string?> ReadLineAsync(string prompt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Console.Write(prompt);
        var line = new StringBuilder();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(40, token);
                    continue;
                }

                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                    return line.ToString();
                if (line.Length == 0 && key.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                    key.Key is ConsoleKey.D or ConsoleKey.Z)
                    return null;
                if (key.Key == ConsoleKey.Backspace && line.Length > 0)
                {
                    line.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    line.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
        finally
        {
            Console.WriteLine();
        }
    }
}
