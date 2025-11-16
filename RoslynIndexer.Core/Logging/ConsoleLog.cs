using System;
using System.Drawing;
using System.IO;

namespace RoslynIndexer.Core.Logging
{
    /// <summary>
    /// Simple console logger with colored output.
    /// Green = OK/info, Yellow = warning, Red = error, DarkGray = debug.
    /// </summary>
    public static class ConsoleLog
    {
        public static void Info(string message)
        {
            Write(message, ConsoleColor.Green, Console.Out);
        }

        public static void Warn(string message)
        {
            Write(message, ConsoleColor.Yellow, Console.Out);
        }

        public static void Error(string message)
        {
            Write(message, ConsoleColor.Red, Console.Error);
        }

        public static void Debug(string message)
        {
            Write(message, ConsoleColor.DarkGray, Console.Out);
        }

        private static void Write(string message, ConsoleColor color, TextWriter writer)
        {
            var prev = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                writer.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = prev;
            }
        }

        public static void Flush()
        {
            Console.Out.Flush();
        }

        public static void InfoLine(string v,int percent)
        {
            var writer = Console.Out;
            var prev = Console.ForegroundColor;
            Random rand = new Random(); // Generator losowy
            ConsoleColor[] colors = { ConsoleColor.Green, ConsoleColor.DarkMagenta, ConsoleColor.Blue }; // Tablica kolorów konsoli
            ConsoleColor randomColor = colors[rand.Next(colors.Length)];
            if (percent == 100) randomColor = ConsoleColor.Green;
            try
            {
                Console.ForegroundColor = randomColor;
                writer.Write(v);
            }
            finally
            {
                Console.ForegroundColor = prev;
            }
        }
    }
}
