# Release Log / 更新日誌 / リリースノート

## Licensing — 2026-07-03

- **Relicensed from MIT to GPLv3.** GimmeCapture bundles and links against FFmpeg built with
  `--enable-gpl` — including the GPL-licensed **x264**/**x265** encoders used for H.264/H.265 —
  so the application as distributed is a combined work that must be under the GPL. See
  [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## v0.50.0 - 2026-07-04

First release since v0.48.0, collecting the compress/editor and recording work built over the
past week (166 commits).

### 🎬 Compress (new tab)
- Import any video and re-encode it smaller entirely in-process (libav): **H.264 / H.265**,
  **compress-to-target-size** (true two-pass for H.264), resolution downscale, **frame-rate cap**,
  encoder preset, CRF, **audio bitrate + mono/stereo mixdown** or drop-audio, custom output path.
- **Batch queue** with parallel encoding, **per-item** output settings + presets, live output-size
  estimate, persistent working directories, and per-row **rotation**.
- **Advanced video editor**: trim / speed / crop / rotate plus Pin-parity annotations, redaction and
  freeze-frame; **inline side-by-side quality compare** with a playhead-following window.

### ⏺️ Recording
- **True per-window capture** via Windows Graphics Capture (WGC), **multi-window** composite and
  separate-file capture, and a **monitor / window capture-scope picker**.
- **Keystroke overlay**, **high-quality GIF** (palettegen/paletteuse) with a **disk-space guard**,
  deterministic audio mixing, webcam PiP (size/shape) with buffer pooling, global **stop hotkey** +
  live **mute**, and an optional pipelined encode path.

### ▶️ Playback
- **GPU hardware-accelerated decode** + frame-drop for smooth large-file preview.

### 🗂️ History
- Compress-style category **tab strip** (Output-copy / Plain-copy).

### 📦 Packaging & licensing
- Optional **Inno Setup installer** (choose install drive) alongside the portable zip.
- **Relicensed MIT → GPLv3** (bundles GPL FFmpeg incl. x264/x265).

### 🧹 Internal
- **Linux port groundwork**: platform-selection seams + stub services (build-shape unchanged,
  Windows-only for now). Tier-2/3/4 refactors (dead-code cleanup, transcoder characterization tests,
  settings split) and a CI Linux compile gate + caching.


## [v0.8.0] - 2026-02-04

### 🎸 Skeletal Theme Enhancement
- **High-Detail Wings**: Integrated skeletal wing assets with direct image rendering for maximum visual fidelity.
- **Corner Aesthetics**: Replaced standard handles with Heart and Skull icons for a distinct "Metal" look.

### 👻 Ghost Mode Implementation
- **Seamless Interaction**: The selection area is now completely transparent and click-through, allowing direct interaction with background applications during capture.

### 📐 Precision Scaling
- **Wing Scale (0.5x - 3.0x)**: Dedicated controls to resize side decorations.
- **Icon Scale (0.4x - 1.0x)**: Independent scaling for corner heart/skull icons to ensure perfect UI balance.

### 🎨 UI & UX Refinement
- **Interactive Numeric Controls**: Added manual text entry support and theme-aware styling to all numeric inputs.
- **Improved Settings Preview**: High-resolution preview window with expanded area to accommodate all scaling options.

### 🌐 Full Trilingual Support
- Updated localization for English, Traditional Chinese, and Japanese across all new customization features.

---

## [v0.7.0] - 2026-02-04 (Trilingual)

### 🎸 New Style: Skeletal & Heavy Metal / 骨格主題 / スケルトンテーマ
- **High-Detail Wings**: Added beautiful skeletal wings in the middle-left and middle-right of the selection. 
  - *ZH*: 導入高細節骨格翅膀，配置於選取區左右兩側。
  - *JA*: 高精細なスカルウィングを導入し、選択範囲の左右に配置しました。
- **Corner Icons**: Replaced generic handles with Hearts and Skulls.
  - *ZH*: 角落手把更換為「愛心」與「骷髏」圖標。
  - *JA*: コーナーハンドルを「ハート」と「スカル」のアイコンに刷新しました。

### 👻 Ghost Mode / 幽靈模式 / ゴーストモード
- **Live Interaction**: The selection center is now truly transparent and click-through, allowing you to interact with background apps (like video players) while snipping.
  - *ZH*: 選取中心區域改為完全可穿透點擊，讓你在擷圖/錄影時仍能操作底層視窗。
  - *JA*: 選択範囲の中央をクリック透過させ、背後のアプリ（動画プレイヤーなど）を直接操作できるようになりました。

### 📐 Scaling System / 比例自訂 / スケーリング
- **Wing Scale (0.5x - 3.0x)**: Customize the size of side wings.
  - *ZH*: 翅膀大小現在可自由調整。
  - *JA*: ウィングのサイズを自由に変更可能になりました。
- **Icon Scale (0.4x - 1.0x)**: Adjust the size of corner Heart/Skull icons.
  - *ZH*: 角落圖標比例現在可獨立調整。
  - *JA*: コーナーアイコンのサイズを調整できるようになりました。

### 🎨 UI Refinements / 介面優化 / インターフェースの改善
- **Enhanced Numeric Input**: New `CompactNumericStep` supports manual typing and matches your Theme Color.
  - *ZH*: 數值輸入框支援手動輸入，並自動套用主題配色。
  - *JA*: 数値入力ボックスが直接入力に対応し、テーマカラーが適用されるようになりました。
- **Settings Preview**: Enlarged style preview (550px) to prevent clipping at high scales.
  - *ZH*: 加大設定預覽視窗至 550px，確保大比例下也不會裁切。
  - *JA*: スケール拡大時の表示切れを防ぐため、プレビュー画面を 550px に拡大しました。

### 🌐 Localization / 語言支援 / ローカライズ
- Full trilingual support for English, Traditional Chinese, and Japanese across all new features.
  - *ZH*: 所有新功能皆完整支援中、英、日三種語言。
  - *JA*: 全ての新機能において、英語、繁体字中国語、日本語のフルローカライズを完了しました。
