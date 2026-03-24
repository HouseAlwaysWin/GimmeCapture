# GimmeCapture Phased Benchmarking Strategy

Based on the architecture of GimmeCapture, which combines local ONNX/AI models, FFmpeg process handling, and Avalonia/ReactiveUI data binding, performance testing should be broken down into structured phases. This ensures we isolate computational bottlenecks from network I/O or rendering latency.

## Proposed Testing Phases

### Phase 1: Core I/O & Foundations
Focus is on high-frequency loops, file system interactions, and configuration serialization.
- **File System Scanning:** The current [GetCurrentRecordingSizeBytes](file:///d:/DotNetProjects/GimmeCapture/src/GimmeCapture/Services/Core/Media/RecordingService.cs#47-84) behavior and temp file enumerations.
- **Settings Serialization:** The overhead of `AppSettingsService` loading and saving [config.json](file:///d:/DotNetProjects/GimmeCapture/config.json).
- **Memory Allocation:** String concatenations and path building inside the core `AIPathService`.

### Phase 2: AI & ML Engine Overheads (C# Layer)
Focus is on the C# side of model invocations, ignoring the actual GPU/CPU inference time (which is internal to ONNX/Ollama).
- **ONNX Session Management:** Testing the allocation and initialization overhead of `InferenceSession` in PaddleOCR and MarianMT.
- **Prompt Generation & String Parsing:** Benchmarking [LLMTranslationEngine](file:///d:/DotNetProjects/GimmeCapture/src/GimmeCapture/Services/Translation/LLMTranslationEngine.cs#15-185) prompt concatenation and JSON output deserialization (extracting `response` fields).
- **Caching Mechanism:** Benchmarking the speed and thread-safety of `InMemoryTranslationCache.TryGet/Set`.

### Phase 3: Media Processing Stream Handling
Focus is on FFmpeg integration and byte processing.
- **FFmpeg Argument Building:** String allocation for [GetEncodingOptions](file:///d:/DotNetProjects/GimmeCapture/src/GimmeCapture/Services/Core/Media/RecordingService.Finalize.cs#258-294) and arguments assembly logic.
- **Stream Parsing ([LineDispatchStream](file:///d:/DotNetProjects/GimmeCapture/src/GimmeCapture/Services/Core/Media/RecordingService.Finalize.cs#462-559)):** Benchmarking real-time UTF8 decoding over standard error/output pipes from [RecordingService.Finalize.cs](file:///d:/DotNetProjects/GimmeCapture/src/GimmeCapture/Services/Core/Media/RecordingService.Finalize.cs).

### Phase 4: ReactiveUI & Presentation
Focus is on ViewModel notifications and UI state.
- **Reactive Interactions:** Benchmarking property change observations in complex views like [FloatingImageViewModel](file:///d:/DotNetProjects/GimmeCapture/src/GimmeCapture/ViewModels/Floating/FloatingImageViewModel.cs#227-307) (especially those hooked into frequent actions).
- **Hotkey Registration Overheads:** Time taken to parse and register complex string commands to actions.

## User Review Required
> [!IMPORTANT]
> Please review this phased approach. To begin, we can expand `GimmeCapture.Benchmarks` to cover **Phase 1** completely (which includes the File IO loop we just analyzed), or jump directly to the AI/Media layers.
> 
> Let me know which phase you would like to implement tests for first!
