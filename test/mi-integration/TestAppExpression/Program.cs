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
            decimal dec = 12345678901234567890123456m;
            decimal longZeroDec = 0.00000000000000000017M;
            decimal shortZeroDec = 0.17M;
            int[] array1 = { 10, 20, 30, 40, 50 };
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
            int e = c + a;
            Console.WriteLine(str1 + str2); // BREAK2
            tc.IncA(); // BREAK3_CALL
            Console.WriteLine($"after inc, a={tc.a}"); // BREAK3
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
            a++;
            Console.WriteLine($"IncA -> {a}");
        }
    }
