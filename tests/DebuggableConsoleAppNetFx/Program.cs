using System;

class Program
{
    static void Main(string[] args)
    {
        var cls = new NetFx.NetFxClass();
        for (int i = 0; i < 500; i++)
        {
            var greeting = cls.GetGreeting(i);
            Console.WriteLine(greeting);
            System.Threading.Thread.Sleep(50);
        }
    }
}
