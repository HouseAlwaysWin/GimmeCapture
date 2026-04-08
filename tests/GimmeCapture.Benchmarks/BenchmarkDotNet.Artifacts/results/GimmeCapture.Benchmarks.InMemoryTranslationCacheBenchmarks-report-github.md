```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 7535HS with Radeon Graphics 3.30GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method    | Mean     | Error    | StdDev   | Median   | Allocated |
|---------- |---------:|---------:|---------:|---------:|----------:|
| CacheHit  | 46.15 ns | 0.926 ns | 0.867 ns | 45.74 ns |         - |
| CacheMiss | 18.44 ns | 0.408 ns | 0.776 ns | 18.07 ns |         - |
| CacheSet  | 54.66 ns | 1.113 ns | 1.891 ns | 54.10 ns |         - |
