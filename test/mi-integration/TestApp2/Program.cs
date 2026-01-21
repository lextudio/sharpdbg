using System;
using System.Threading;

namespace TestApp2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("TestApp2 starting");
            int x = 10;
            x += 5; // BREAKPOINT_BP
            Console.WriteLine($"x={x}");
            Thread.Sleep(500);
            Console.WriteLine("TestApp2 exiting");
        }
    }
}
