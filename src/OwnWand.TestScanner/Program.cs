using System;
using System.Diagnostics;
using OwnWand.Injector;

namespace OwnWand.TestScanner;

class Program
{
    static void Main(string[] args)
    {
        var processes = Process.GetProcessesByName("Backrooms-Win64-Shipping");
        if (processes.Length == 0) return;
        var target = processes[0];

        Console.WriteLine("--- Testing Candidate Health/Damage Signatures ---");

        string[] candidates = new string[]
        {
            "89 83 24 01 00 00 48 8B 03",      // Preset God Mode pattern
            "F3 0F 11 83 30 01 00 00 F3",      // Preset Infinite Stamina pattern
            "F3 0F 11 83 A0 01 00 00",         // Matched health write candidate 1
            "F3 0F 11 87 A0 01 00 00"          // Matched health write candidate 2
        };

        foreach (var pattern in candidates)
        {
            ulong address = MemoryScanner.ScanPattern(target.Id, "Backrooms-Win64-Shipping.exe", pattern, out string err);
            if (address != 0)
            {
                Console.WriteLine($"[MATCH] Pattern '{pattern}' matched at 0x{address:X16}");
            }
            else
            {
                Console.WriteLine($"[FAIL] Pattern '{pattern}' failed to match. Error: {err}");
            }
        }
    }
}
