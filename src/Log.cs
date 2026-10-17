using System;
using System.Collections.Concurrent;
using System.Threading;

namespace KbmOverlay
{
    // Console output goes through one background thread. When text is selected in the console
    // window, writes block; this keeps that from stalling input handling.
    internal static class Log
    {
        private struct Line
        {
            public ConsoleColor? Color;
            public string Text;
        }

        private static readonly BlockingCollection<Line> Lines = new BlockingCollection<Line>();

        static Log()
        {
            var thread = new Thread(() =>
            {
                foreach (Line line in Lines.GetConsumingEnumerable())
                {
                    if (line.Color.HasValue) Console.ForegroundColor = line.Color.Value;
                    Console.WriteLine(line.Text);
                    if (line.Color.HasValue) Console.ResetColor();
                }
            });
            thread.IsBackground = true;
            thread.Name = "Log";
            thread.Start();
        }

        public static void Info(string text) => Lines.Add(new Line { Text = text });
        public static void Good(string text) => Lines.Add(new Line { Color = ConsoleColor.Green, Text = text });
        public static void Accent(string text) => Lines.Add(new Line { Color = ConsoleColor.Cyan, Text = text });
        public static void Warn(string text) => Lines.Add(new Line { Color = ConsoleColor.Yellow, Text = text });
        public static void Error(string text) => Lines.Add(new Line { Color = ConsoleColor.Red, Text = text });

        // Waits until queued lines are on screen (before the program exits).
        public static void Flush()
        {
            for (int i = 0; i < 50 && Lines.Count > 0; i++) Thread.Sleep(20);
        }
    }
}
