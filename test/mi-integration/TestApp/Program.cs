using System;
using System.Threading;

namespace MiIntegrationTestApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("TestApp starting");
            // breakpoint target line
            Console.WriteLine("BreakHere"); // BREAK_LINE
            Console.WriteLine("After breakpoint");
            // keep process alive briefly so debugger can continue and exit
            Thread.Sleep(2000);
            Console.WriteLine("TestApp exiting");
        }
    }
}
