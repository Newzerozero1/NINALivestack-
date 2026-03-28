using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    /// <summary>
    /// Uploads stacked images to Cloudflare R2 for star party sharing.
    /// Overlays target name (bottom-left) and overlay text (bottom-right).
    /// Uses AWS Signature V4 signing — no SDK dependencies.
    /// All credentials loaded from PluginSettings (no hardcoded secrets).
    /// </summary>
    public class CloudUploader {

        private const string Region = "auto";
        private const string Service = "s3";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static bool _uploading = false;

        /// <summary>Current settings — set by the VM when settings are loaded or changed.</summary>
        public static PluginSettings Settings { get; set; }

        public static bool Enabled { get; set; } = false;

        /// <summary>
        /// Last upload error message, or empty string if last upload succeeded.
        /// </summary>
        public static string LastError { get; private set; } = "";

        /// <summary>
        /// Render the display bitmap with text overlays and upload as JPEG.
        /// Must be called from UI thread (BitmapSource is not frozen cross-thread).
        /// </summary>
        public static void UploadIfEnabled(BitmapSource displayImage, string targetName) {
            if (!Enabled || displayImage == null) return;
            if (_uploading) return;

            var s = Settings;
            if (s == null || !s.HasR2Credentials) {
                LastError = "R2 not configured";
                return;
            }

            try {
                byte[] jpeg = RenderWithOverlay(displayImage, targetName, s);
                if (jpeg == null || jpeg.Length == 0) return;
                Task.Run(() => UploadAllAsync(jpeg, s));
            } catch (Exception ex) {
                LastError = $"Render error: {ex.Message}";
                Logger.Warning($"LiveStack: R2 render error: {ex.Message}");
            }
        }

        private static byte[] RenderWithOverlay(BitmapSource source, string targetName, PluginSettings s) {
            int w = source.PixelWidth;
            int h = source.PixelHeight;

            double fontSize = Math.Max(16, h * 0.022);
            double margin = fontSize * 0.6;
            double shadowOffset = Math.Max(1.5, fontSize * 0.06);

            string fontName = !string.IsNullOrWhiteSpace(s.OverlayFont) ? s.OverlayFont : "Monotype Corsiva";
            string overlayText = s.OverlayText ?? "";

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawImage(source, new Rect(0, 0, w, h));

                if (s.OverlayEnabled) {
                    var typeface = new Typeface(new FontFamily($"{fontName}, Segoe Script, Segoe UI"),
                        FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
                    var shadowBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
                    double textY = h - fontSize * 1.6;

                    if (!string.IsNullOrEmpty(targetName)) {
                        var targetFmt = new FormattedText(targetName, CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, 96);
                        var targetShadow = new FormattedText(targetName, CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, typeface, fontSize, shadowBrush, 96);
                        dc.DrawText(targetShadow, new Point(margin + shadowOffset, textY + shadowOffset));
                        dc.DrawText(targetFmt, new Point(margin, textY));
                    }

                    if (!string.IsNullOrEmpty(overlayText)) {
                        var igText = new FormattedText(overlayText, CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, 96);
                        var igShadow = new FormattedText(overlayText, CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, typeface, fontSize, shadowBrush, 96);
                        dc.DrawText(igShadow, new Point(w - igText.Width - margin + shadowOffset, textY + shadowOffset));
                        dc.DrawText(igText, new Point(w - igText.Width - margin, textY));
                    }
                }
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// Generate the viewer HTML with the user's PublicUrl for version.txt polling.
        /// Pinch zoom + one-finger pan when zoomed. New frames swap in without moving.
        /// </summary>
        private static string GenerateViewerHtml(PluginSettings s) {
            string baseUrl = "";
            if (!string.IsNullOrWhiteSpace(s.R2PublicUrl))
                baseUrl = s.R2PublicUrl.TrimEnd('/') + "/";

            return @"<!DOCTYPE html>
<html><head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover"">
<meta name=""apple-mobile-web-app-capable"" content=""yes"">
<meta name=""mobile-web-app-capable"" content=""yes"">
<meta name=""apple-mobile-web-app-status-bar-style"" content=""black"">
<title>Live Stack</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
html,body{width:100%;height:100dvh;background:#000;overflow:hidden;touch-action:none;-webkit-user-select:none;user-select:none}
#img{position:absolute;transform-origin:0 0;will-change:transform}
.hint{position:fixed;bottom:10px;left:50%;transform:translateX(-50%);color:#555;font:11px sans-serif;
  text-align:center;pointer-events:none;transition:opacity 2s;z-index:10}
</style>
</head><body>
<img id=""img"" src=""" + baseUrl + @"latest.jpg"">
<div class=""hint"" id=""hint"">Pinch to zoom &#8226; Drag to pan &#8226; Rotate for best view</div>
<script>
(function(){
  var img=document.getElementById('img');
  var s=1, tx=0, ty=0, fitS=1;
  var pinching=false, lastTap=0, ready=false;
  var t0x, t0y, s0, tx0, ty0, d0;

  function put(){ img.style.transform='translate('+tx+'px,'+ty+'px) scale('+s+')'; }

  function clamp(){
    var vw=window.innerWidth, vh=window.innerHeight;
    var iw=img.naturalWidth*s, ih=img.naturalHeight*s;
    if(iw<=vw) tx=(vw-iw)/2; else tx=Math.min(0,Math.max(vw-iw,tx));
    if(ih<=vh) ty=(vh-ih)/2; else ty=Math.min(0,Math.max(vh-ih,ty));
  }

  function fit(){
    var vw=window.innerWidth, vh=window.innerHeight;
    var nw=img.naturalWidth, nh=img.naturalHeight;
    if(!nw||!nh) return;
    fitS=Math.min(vw/nw, vh/nh);
    s=fitS; tx=(vw-nw*s)/2; ty=(vh-nh*s)/2; put();
  }

  // Only fit on FIRST load — subsequent frame updates keep current zoom/pan
  // Only fit on first load — subsequent frames just swap pixels, transform untouched
  img.onload=function(){ if(!ready){ fit(); ready=true; } };
  window.addEventListener('resize',fit);

  function dist(a,b){var dx=a.clientX-b.clientX,dy=a.clientY-b.clientY;return Math.sqrt(dx*dx+dy*dy);}

  document.addEventListener('touchstart',function(e){
    e.preventDefault();
    if(e.touches.length===2){
      pinching=true;
      d0=dist(e.touches[0],e.touches[1]); s0=s; tx0=tx; ty0=ty;
      t0x=(e.touches[0].clientX+e.touches[1].clientX)/2;
      t0y=(e.touches[0].clientY+e.touches[1].clientY)/2;
    }else if(e.touches.length===1&&!pinching){
      t0x=e.touches[0].clientX; t0y=e.touches[0].clientY; tx0=tx; ty0=ty;
    }
  },{passive:false});

  document.addEventListener('touchmove',function(e){
    e.preventDefault();
    if(e.touches.length===2){
      var d=dist(e.touches[0],e.touches[1]);
      var ns=Math.max(fitS, Math.min(20, s0*(d/d0)));
      var mx=(e.touches[0].clientX+e.touches[1].clientX)/2;
      var my=(e.touches[0].clientY+e.touches[1].clientY)/2;
      var imgX=(t0x-tx0)/s0, imgY=(t0y-ty0)/s0;
      tx=mx-imgX*ns; ty=my-imgY*ns; s=ns;
      clamp(); put();
    }else if(e.touches.length===1&&!pinching){
      // Only pan if zoomed in past fit
      if(s>fitS*1.01){
        tx=tx0+(e.touches[0].clientX-t0x);
        ty=ty0+(e.touches[0].clientY-t0y);
        clamp(); put();
      }
    }
  },{passive:false});

  document.addEventListener('touchend',function(e){
    if(e.touches.length<2) pinching=false;
    if(e.touches.length===1){ t0x=e.touches[0].clientX; t0y=e.touches[0].clientY; tx0=tx; ty0=ty; }
    if(e.touches.length===0){ var now=Date.now(); if(now-lastTap<300)fit(); lastTap=now; }
  });

  var v='';
  async function poll(){
    try{
      var r=await fetch('" + baseUrl + @"version.txt?t='+Date.now());
      var t=await r.text();
      if(t!==v){
        v=t;
        // Preload in background so the visible image never flashes blank
        var pre=new Image();
        pre.onload=function(){ img.src=pre.src; };
        pre.src='" + baseUrl + @"latest.jpg?t='+Date.now();
      }
    }catch(e){}
  }
  setInterval(poll,5000);
  setTimeout(function(){document.getElementById('hint').style.opacity='0';},5000);
})();
</script>
</body></html>";
        }

        private static async Task UploadAllAsync(byte[] jpegBytes, PluginSettings s) {
            if (jpegBytes == null || jpegBytes.Length == 0) return;
            _uploading = true;

            try {
                await PutObject(s, "latest.jpg", jpegBytes, "image/jpeg");

                string version = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                await PutObject(s, "version.txt", Encoding.UTF8.GetBytes(version), "text/plain");

                string html = GenerateViewerHtml(s);
                await PutObject(s, "index.html", Encoding.UTF8.GetBytes(html), "text/html");

                LastError = "";
                Logger.Info($"LiveStack: Uploaded {jpegBytes.Length / 1024}KB to R2");
            } catch (Exception ex) {
                LastError = $"Upload failed: {ex.Message}";
                Logger.Warning($"LiveStack: R2 upload error: {ex.Message}");
            } finally {
                _uploading = false;
            }
        }

        private static async Task PutObject(PluginSettings s, string key, byte[] data, string contentType) {
            string endpoint = s.R2Endpoint.TrimEnd('/');
            string bucket = s.R2Bucket.Trim();
            string accessKey = s.R2AccessKey.Trim();
            string secretKey = s.R2SecretKey.Trim();

            string url = $"{endpoint}/{bucket}/{key}";
            string now = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string date = now.Substring(0, 8);

            string payloadHash = Sha256Hex(data);
            string canonicalUri = $"/{bucket}/{key}";
            string host = new Uri(endpoint).Host;
            string signedHeaders = "content-type;host;x-amz-content-sha256;x-amz-date";
            string canonicalHeaders =
                $"content-type:{contentType}\n" +
                $"host:{host}\n" +
                $"x-amz-content-sha256:{payloadHash}\n" +
                $"x-amz-date:{now}\n";

            string canonicalRequest =
                $"PUT\n{canonicalUri}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

            string credentialScope = $"{date}/{Region}/{Service}/aws4_request";
            string stringToSign =
                $"AWS4-HMAC-SHA256\n{now}\n{credentialScope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";

            byte[] kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), date);
            byte[] kRegion = HmacSha256(kDate, Region);
            byte[] kService = HmacSha256(kRegion, Service);
            byte[] kSigning = HmacSha256(kService, "aws4_request");

            string signature = BitConverter.ToString(
                HmacSha256(kSigning, stringToSign)).Replace("-", "").ToLower();

            string authorization =
                $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, " +
                $"SignedHeaders={signedHeaders}, Signature={signature}";

            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            request.Headers.TryAddWithoutValidation("x-amz-date", now);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) {
                string body = await response.Content.ReadAsStringAsync();
                throw new Exception($"{response.StatusCode}: {body}");
            }
        }

        private static string Sha256Hex(byte[] data) {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLower();
        }

        private static byte[] HmacSha256(byte[] key, string data) {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }
    }
}
