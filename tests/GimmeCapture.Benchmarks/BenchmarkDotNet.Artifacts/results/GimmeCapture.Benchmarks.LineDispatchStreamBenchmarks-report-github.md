```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 7535HS with Radeon Graphics 3.30GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method        | Mean     | Error   | StdDev  | Gen0    | Gen1   | Allocated |
|-------------- |---------:|--------:|--------:|--------:|-------:|----------:|
| ProcessStream | 108.9 μs | 2.11 μs | 2.60 μs | 10.7422 | 0.2441 |   87.9 KB |
