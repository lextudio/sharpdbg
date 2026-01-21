using System;
using System.Threading;

namespace MiIntegrationTestApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("TestApp starting");
            // breakpoint target line - calls user method that can be stepped into
            UserMethod(); // BREAK_LINE (line 12)
            Console.WriteLine("After breakpoint");
            // keep process alive briefly so debugger can continue and exit
            Thread.Sleep(2000);
            Console.WriteLine("TestApp exiting");
        }

        static void UserMethod()
        {
            Console.WriteLine("Inside UserMethod");
        }
    }
}
