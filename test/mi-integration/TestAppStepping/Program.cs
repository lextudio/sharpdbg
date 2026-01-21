using System;
using System.Threading;

namespace TestAppStepping
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Stepping test start");

            // step 1
            Foo(); // BREAK_STEP_INTO

            // step over
            Bar(); // BREAK_STEP_OVER

            // end
            Console.WriteLine("Stepping test end");
            Thread.Sleep(500);
        }

        static void Foo()
        {
            Console.WriteLine("In Foo");
        }

        static void Bar()
        {
            int x = 1;
            x += 2; // LINE_INSIDE_BAR
            Console.WriteLine(x);
        }
    }
}
