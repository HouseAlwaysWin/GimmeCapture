```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.8037/25H2/2025Update/HudsonValley2)
AMD Ryzen 5 7535HS with Radeon Graphics 3.30GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.1 (10.0.1, 10.0.125.57005), X64 RyuJIT x86-64-v3


```
| Method    | Mean     | Error    | StdDev   | Median   | Gen0   | Gen1   | Allocated |
|---------- |---------:|---------:|---------:|---------:|-------:|-------:|----------:|
| SAM2Paths | 125.3 ns |  2.47 ns |  6.56 ns | 122.8 ns | 0.1855 | 0.0095 |   1.52 KB |
| OCRPaths  | 183.2 ns |  9.37 ns | 26.89 ns | 182.4 ns | 0.2332 | 0.0165 |   1.91 KB |
| NmtPaths  | 394.9 ns | 16.14 ns | 46.83 ns | 398.1 ns | 0.3633 | 0.0515 |   2.97 KB |
