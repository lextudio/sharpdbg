using System;
using System.Threading;

namespace TestApp1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("TestApp1 starting");
            int i = 1;
            i++; // BREAKPOINT_BP
            Console.WriteLine($"i={i}");
            Thread.Sleep(500);
            Console.WriteLine("TestApp1 exiting");
        }
    }
}
