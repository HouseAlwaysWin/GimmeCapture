Native FFmpeg DLLs (GPL shared build) for FFmpeg.AutoGen 8.x.

Populate this folder before release builds:
  powershell -ExecutionPolicy Bypass -File ../../../scripts/ensure-ffmpeg-libs.ps1

Or manually extract ffmpeg-master-latest-win64-gpl-shared.zip from BtbN FFmpeg-Builds
and copy all *.dll from the archive's bin\ folder into this directory.

Required at runtime next to GimmeCapture.exe under ffmpeg-lib\ (see FFmpegRuntime).
