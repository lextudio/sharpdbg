using System;
using System.Threading;

namespace TestAppExpression;

    public class Program
    {
        public static string Greeting => "hello";

        public static int Multiply(int x, int y) => x * y;

        public static void Main(string[] args)
        {
            Console.WriteLine("TestAppExpression starting");
            int a = 10;
            int b = 11;
            int[] valueArray = { 10, 20, 30 };
            bool isTrue = true;
            bool isFalse = false;
            int? optionalValue = null;
            int fallbackValue = 5;
            TestStruct tc = new TestStruct(a + 1, b);
            string str1 = "string1";
            string str2 = "string2";
            int c = tc.b + b; // BREAK1
            int d = 99;
            int e = c + a; // BREAK2
            Console.WriteLine(str1 + str2);
            tc.IncA(); // BREAK3
            Console.WriteLine($"after inc, a={tc.a}");
            Thread.Sleep(500);
            Console.WriteLine("TestAppExpression exiting");
            Thread.Sleep(500);
        }
    }

    public struct TestStruct
    {
        public int a;
        public int b;

        public TestStruct(int x, int y)
        {
            a = x;
            b = y;
        }

        public int Sum => a + b;

        public void IncA()
        {
            a++; // BREAK3_INSIDE
            Console.WriteLine($"IncA -> {a}");
        }
    }
