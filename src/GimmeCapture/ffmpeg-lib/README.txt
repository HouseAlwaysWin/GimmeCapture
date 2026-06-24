Native FFmpeg GPL shared build for FFmpeg.AutoGen 8.x:
  - all *.dll        -> in-process libav (recording, preview; see FFmpegRuntime)
  - ffmpeg.exe       -> CLI the pin video export shells out to (CliWrap)
  - ffprobe.exe      -> CLI probing (optional)

Populate this folder before release builds:
  powershell -ExecutionPolicy Bypass -File ../../../scripts/ensure-ffmpeg-libs.ps1

Or manually extract ffmpeg-master-latest-win64-gpl-shared.zip from BtbN FFmpeg-Builds
and copy all *.dll plus ffmpeg.exe/ffprobe.exe from the archive's bin\ folder here.

Required at runtime next to GimmeCapture.exe under ffmpeg-lib\ (see FFmpegRuntime).
