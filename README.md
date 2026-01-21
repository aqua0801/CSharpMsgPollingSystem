```csharp
using System.Diagnostics;
using MsgPollingSystem;

var service = new ChannelMessageService<int, int>(new Driver());
service.Start();

var result = await service.SubmitAsync(42);
Console.WriteLine(result);
// 420

await Benchmark.RunAsync();

/*
---- Benchmark Result ----
Total Requests : 1,000,000
Parallelism    : 8
Total Time     : 865.09 ms
Avg Latency    : 0.865 μs
Throughput     : 1,155,955 req/sec

cpu : i3-10100f
os : win10
build : debug
--------------------------
---- Benchmark Result ----
Total Requests : 1,000,000
Parallelism    : 8
Total Time     : 709.84 ms
Avg Latency    : 0.710 μs
Throughput     : 1,408,762 req/sec
build : publish/release
*/

public sealed class Driver : IDriver<int, int>
{
    public int Execute(int data)
    {
        return data * 10;
    }
}

public static class Benchmark
{
    public static async Task RunAsync()
    {
        const int totalRequests = 1_000_000;
        int parallelProducers = Environment.ProcessorCount;

        var service = new ChannelMessageService<int, int>(new Driver());
        service.Start();

        var sw = Stopwatch.StartNew();

        var producerTasks = new Task[parallelProducers];

        for (int p = 0; p < parallelProducers; p++)
        {
            producerTasks[p] = Task.Run(async () =>
            {
                int perProducer = totalRequests / parallelProducers;

                var tasks = new Task<int>[perProducer];
                var rand = new Random();

                for (int i = 0; i < perProducer; i++)
                {
                    int value = rand.Next(0, 10_000);
                    tasks[i] = service.SubmitAsync(value);
                }

                await Task.WhenAll(tasks);
            });
        }

        await Task.WhenAll(producerTasks);

        sw.Stop();
        service.Stop();

        double totalMs = sw.Elapsed.TotalMilliseconds;
        double avgUs = (totalMs * 1000) / totalRequests;
        double throughput = totalRequests / sw.Elapsed.TotalSeconds;

        Console.WriteLine("---- Benchmark Result ----");
        Console.WriteLine($"Total Requests : {totalRequests:N0}");
        Console.WriteLine($"Parallelism    : {parallelProducers}");
        Console.WriteLine($"Total Time     : {totalMs:F2} ms");
        Console.WriteLine($"Avg Latency    : {avgUs:F3} µs");
        Console.WriteLine($"Throughput     : {throughput:N0} req/sec");
    }
}
```
