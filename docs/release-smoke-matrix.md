# Release Smoke Matrix

Run this matrix against the release candidate and the final build produced from
the same commit.

| Area | Cases | Expected result |
| --- | --- | --- |
| Display | 100%, 125%, 150%, mixed-DPI monitors; negative monitor coordinates | Selection, annotations, and pinned windows remain aligned |
| Image pin | Copy, save, crop, annotation, close during AI work | No crash, stale overlay, or locked output file |
| Video pin | Play, pause, seek, repeat, close during playback | No unobserved exception, file lock, audio leak, or pooled buffer leak |
| Recording | GIF, MP4 H.264, MP4 H.265, WebM; system audio on/off | Output opens, duration is correct, and temporary files are removed |
| OCR | Auto plus every supported source language | Text boxes align and cancellation releases native sessions |
| Translation | Llama presets, retry, unacceptable output fallback | Sanitized output matches characterization tests |
| Downloads | Success, cancel, retry, truncated response, checksum mismatch | Existing artifacts remain intact until verified replacement |
| Update | Missing manifest, checksum mismatch, path traversal, launch failure | Existing installation remains runnable and failure is reported |
| Diagnostics | Copy diagnostics after a handled failure | Version, OS, renderer, module states, and anonymous error types are present |
