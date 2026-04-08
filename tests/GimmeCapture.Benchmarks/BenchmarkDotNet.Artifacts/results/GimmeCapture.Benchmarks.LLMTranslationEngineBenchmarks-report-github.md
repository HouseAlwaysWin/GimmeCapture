```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 7535HS with Radeon Graphics 3.30GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method                    | Mean     | Error   | StdDev  | Gen0   | Allocated |
|-------------------------- |---------:|--------:|--------:|-------:|----------:|
| TranslateAndParseOverhead | 306.6 ns | 4.82 ns | 6.92 ns | 0.0801 |     672 B |
