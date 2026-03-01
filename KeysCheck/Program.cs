using System;
using Avalonia.Input;

class Program {
    static void Main() {
        foreach (var k in Enum.GetNames(typeof(Key))) {
            if (k.Contains("Media") || k.Contains("Play") || k.Contains("Pause")) {
                Console.WriteLine(k);
            }
        }
    }
}
