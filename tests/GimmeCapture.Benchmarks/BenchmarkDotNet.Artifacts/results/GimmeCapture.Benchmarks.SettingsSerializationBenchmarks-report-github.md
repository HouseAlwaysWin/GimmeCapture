```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 7535HS with Radeon Graphics 3.30GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method              | Mean      | Error     | StdDev    | Median    | Gen0   | Allocated |
|-------------------- |----------:|----------:|----------:|----------:|-------:|----------:|
| SerializeSettings   |  6.970 μs | 0.1315 μs | 0.2123 μs |  6.888 μs | 0.7477 |   6.16 KB |
| DeserializeSettings | 11.305 μs | 0.2250 μs | 0.4596 μs | 11.100 μs | 0.4578 |   3.75 KB |
