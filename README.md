# NinaLiveStack

A live stacking plugin for [N.I.N.A. 3.x](https://nighttime-imaging.eu/) — designed for star parties. Load or capture FITS files, and NinaLiveStack aligns, stacks, stretches, and displays a growing image in real time. Optionally broadcast the result to a web page so attendees can follow along on their phones.

## Features

- **Live stacking** — frames are debayered, calibrated (dark/flat), aligned, and accumulated as they arrive
- **Star alignment** — triangle matching with plate solve fallback (works with PlateSolve3, ASTAP, or any solver configured in NINA)
- **Meridian flip handling** — automatically detects and rotates post-flip frames
- **Arcsinh stretch** — luminance-linked stretch that preserves color balance across the brightness range
- **Star reduction** — two-phase detection (linear) + application (post-stretch) so nebula detail is never touched
- **Subtractive white balance** — equalizes channel backgrounds without scaling signal (critical for dual-band filters)
- **Satellite trail masking** — 15σ rejection
- **Hot/cold pixel removal**
- **Bad frame detection** — automatic quality outlier flagging with one-click rebuild
- **Fullscreen presentation window** — target name, frame count, SNR overlay, touch-friendly
- **Web broadcasting** — upload to Cloudflare R2 with text overlay; attendees scan a QR code to watch live
- **32-bit FITS save** — preserve full dynamic range of the stacked result

## Requirements

- N.I.N.A. 3.0 or later (.NET 8)
- Windows 10/11 (x64)
- A plate solver configured in NINA (PlateSolve3 recommended) — optional but improves alignment after meridian flips

## Installation

1. Download `NinaLiveStack.dll`
2. Copy to `%localappdata%\NINA\Plugins\3.0.0\`
3. Restart NINA
4. Open the **Live Stack** dockable panel from the NINA sidebar

## Quick Start

1. Click **Start** to begin listening for new images from your camera
2. Or click **Load** to stack a folder of existing FITS files
3. Adjust **Stretch Factor** to taste (higher = more faint detail visible)
4. Click **Full** for a presentation-ready fullscreen window

## Web Broadcasting Setup

This lets star party attendees view your live stack on their phones. It works with any S3-compatible storage provider:

- **Cloudflare R2** — free tier (10 GB/month), easiest setup
- **AWS S3** — widely supported, pay-per-use
- **Backblaze B2** — inexpensive, S3-compatible API
- **DigitalOcean Spaces**, **Wasabi**, **MinIO**, or any S3-compatible service

### Setup Steps

1. **Create a bucket** in your storage provider
2. **Enable public access** so viewers can load images from it
3. **Create an API key** with read+write permission for the bucket
4. **Find your S3 endpoint URL** in your provider's dashboard
5. In NinaLiveStack, click the **⚙** gear button and fill in all four fields (endpoint, bucket, access key, secret key) plus the public URL
6. Click **Save**, then check the **Broadcast** checkbox

The settings dialog has step-by-step guidance and examples for each field.

### Apple "Malware Warning"

Apple devices may flag shared domains (like Cloudflare's `pub-xxx.r2.dev`) as suspicious. Setting up a custom domain in your storage provider fixes this.

## Calibration

- Click **Dark** to load a master dark frame (FITS)
- Click **Flat** to load a master flat frame (FITS)
- Click **Clr Cal** to remove calibration
- Status shows `Dark ✓ | Flat ✓` when loaded

## Tips

- **Stretch Factor 100** is a good starting point. Go higher (200–400) for faint nebulae.
- **Star Reduce** at 0.2–0.4 tightens stars without affecting nebula structure.
- The **Advanced** expander has brightness, contrast, black clip, RGB balance, and crop controls.
- **Crop** with lock mode trims all edges equally — great for removing stacking artifacts at borders.
- **Rebuild** re-stacks from scratch excluding flagged bad frames.
- When loading files, frames with "BAD" in the filename are automatically skipped.

## Building from Source

1. Install Visual Studio 2022 with .NET 8 SDK
2. Install N.I.N.A. 3.x to the default location
3. Open `NinaLiveStack.sln`
4. Build (x64, Debug or Release)
5. The post-build step copies the DLL to NINA's plugin folder automatically

## License

[Mozilla Public License 2.0](LICENSE.txt) — same as NINA.
