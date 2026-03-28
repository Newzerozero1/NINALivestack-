using System;
using System.IO;
using System.Text;
using NINA.Core.Utility;

namespace NinaLiveStack.Engine {

    public static class FitsReader {

        public static FitsImage ReadFits(string filePath) {
            // Validate file exists and is not obviously wrong
            if (!File.Exists(filePath))
                throw new Exception("File not found");

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 2880)
                throw new Exception("File too small to be a valid FITS file (minimum 2880 bytes for one header block)");

            // Check for common wrong file types by reading magic bytes
            byte[] magic;
            using (var peek = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
                magic = new byte[Math.Min(8, fileInfo.Length)];
                peek.Read(magic, 0, magic.Length);
            }
            if (magic.Length >= 4 && magic[0] == 0xFF && magic[1] == 0xD8)
                throw new Exception("This is a JPEG image, not a FITS file. NinaLiveStack needs raw FITS files from your camera.");
            if (magic.Length >= 4 && magic[0] == 0x89 && magic[1] == 0x50 && magic[2] == 0x4E && magic[3] == 0x47)
                throw new Exception("This is a PNG image, not a FITS file. NinaLiveStack needs raw FITS files from your camera.");
            if (magic.Length >= 4 && magic[0] == 0x49 && magic[1] == 0x49 && magic[2] == 0x2A && magic[3] == 0x00)
                throw new Exception("This is a TIFF image, not a FITS file. NinaLiveStack needs raw FITS files from your camera.");
            // FITS files must start with "SIMPLE  ="
            string header = Encoding.ASCII.GetString(magic, 0, Math.Min(8, magic.Length));
            if (!header.StartsWith("SIMPLE"))
                throw new Exception("Not a valid FITS file — header doesn't start with SIMPLE keyword. Check you selected the right file.");

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);

            int bitpix = 0; int naxis1 = 0, naxis2 = 0;
            double bzero = 0, bscale = 1; string bayerPat = ""; string objectName = "";
            string objctra = ""; string objctdec = "";
            double raNum = double.NaN, decNum = double.NaN;
            double focalLen = double.NaN, pixelSizeUm = double.NaN;
            bool endFound = false;

            while (!endFound) {
                byte[] block = reader.ReadBytes(2880);
                if (block.Length < 2880) throw new Exception("FITS file appears corrupted — header block is incomplete");
                for (int i = 0; i < 36; i++) {
                    string card = Encoding.ASCII.GetString(block, i * 80, 80);
                    string keyword = card.Substring(0, 8).Trim();
                    if (keyword == "END") { endFound = true; break; }
                    if (card.Length > 10 && card[8] == '=' && card[9] == ' ') {
                        string valueStr = card.Substring(10, 70).Split('/')[0].Trim().Trim('\'').Trim();
                        switch (keyword) {
                            case "BITPIX": int.TryParse(valueStr, out bitpix); break;
                            case "NAXIS1": int.TryParse(valueStr, out naxis1); break;
                            case "NAXIS2": int.TryParse(valueStr, out naxis2); break;
                            case "BZERO": double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out bzero); break;
                            case "BSCALE": double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out bscale); break;
                            case "BAYERPAT": bayerPat = valueStr.ToUpper(); break;
                            case "COLORTYP": if (string.IsNullOrEmpty(bayerPat)) bayerPat = valueStr.ToUpper(); break;
                            case "CFA-TYPE": if (string.IsNullOrEmpty(bayerPat)) bayerPat = valueStr.ToUpper(); break;
                            case "OBJECT": objectName = valueStr; break;
                            case "OBJCTRA": objctra = valueStr; break;
                            case "OBJCTDEC": objctdec = valueStr; break;
                            case "RA":
                                double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out raNum);
                                break;
                            case "DEC":
                                double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out decNum);
                                break;
                            case "FOCALLEN":
                                double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out focalLen);
                                break;
                            case "XPIXSZ":
                                double.TryParse(valueStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out pixelSizeUm);
                                break;
                        }
                    }
                }
            }

            if (naxis1 == 0 || naxis2 == 0) throw new Exception($"FITS file has no image data (dimensions {naxis1}x{naxis2}). This might be a header-only file or a data table.");
            Logger.Info($"FITS: {Path.GetFileName(filePath)} {naxis1}x{naxis2} BITPIX={bitpix} BAYER={bayerPat}");

            int pixelCount = naxis1 * naxis2;
            ushort[] rawData = ReadPixelData(reader, pixelCount, bitpix, bzero, bscale);

            if (!string.IsNullOrEmpty(bayerPat) && (bayerPat == "RGGB" || bayerPat == "BGGR" || bayerPat == "GRBG" || bayerPat == "GBRG")) {
                Logger.Info($"FITS: Debayering with pattern {bayerPat}");
                var img = DebayerImage(rawData, naxis1, naxis2, bayerPat);
                img.ObjectName = objectName;
                img.FocalLength = focalLen;
                img.PixelSizeUm = pixelSizeUm;
                AssignCoordinates(img, objctra, objctdec, raNum, decNum);
                return img;
            }

            var monoImg = new FitsImage(rawData, rawData, rawData, naxis1, naxis2) { ObjectName = objectName };
            monoImg.FocalLength = focalLen;
            monoImg.PixelSizeUm = pixelSizeUm;
            AssignCoordinates(monoImg, objctra, objctdec, raNum, decNum);
            return monoImg;
        }

        /// <summary>
        /// Parse and assign RA/DEC coordinates from FITS header values.
        /// Prefers OBJCTRA/OBJCTDEC (sexagesimal strings like "22 59 20.88"),
        /// falls back to RA/DEC numeric keywords (degrees).
        /// </summary>
        private static void AssignCoordinates(FitsImage img, string objctra, string objctdec,
                                               double raNum, double decNum) {
            // Try OBJCTRA/OBJCTDEC first (NINA writes these as sexagesimal strings)
            if (!string.IsNullOrWhiteSpace(objctra) && !string.IsNullOrWhiteSpace(objctdec)) {
                double ra = ParseSexagesimal(objctra);
                double dec = ParseSexagesimalDec(objctdec);
                if (!double.IsNaN(ra) && !double.IsNaN(dec)) {
                    img.RaHours = ra;
                    img.DecDeg = dec;
                    Logger.Info($"FITS: Coordinates from OBJCTRA/OBJCTDEC: RA={ra:F4}h DEC={dec:F4}°");
                    return;
                }
            }

            // Fallback: RA/DEC numeric (degrees)
            if (!double.IsNaN(raNum) && !double.IsNaN(decNum)) {
                img.RaHours = raNum / 15.0; // Convert degrees to hours
                img.DecDeg = decNum;
                Logger.Info($"FITS: Coordinates from RA/DEC: RA={img.RaHours:F4}h DEC={img.DecDeg:F4}°");
            }
        }

        /// <summary>
        /// Parse sexagesimal RA string "HH MM SS.ss" to decimal hours.
        /// </summary>
        private static double ParseSexagesimal(string s) {
            try {
                string[] parts = s.Trim().Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return double.NaN;
                double h = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                double m = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                double sec = parts.Length >= 3
                    ? double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
                return h + m / 60.0 + sec / 3600.0;
            } catch { return double.NaN; }
        }

        /// <summary>
        /// Parse sexagesimal DEC string "+DD MM SS.ss" to decimal degrees.
        /// Handles leading +/- sign.
        /// </summary>
        private static double ParseSexagesimalDec(string s) {
            try {
                s = s.Trim();
                double sign = 1.0;
                if (s.StartsWith("-")) { sign = -1.0; s = s.Substring(1).Trim(); }
                else if (s.StartsWith("+")) { s = s.Substring(1).Trim(); }
                string[] parts = s.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return double.NaN;
                double d = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                double m = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                double sec = parts.Length >= 3
                    ? double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
                return sign * (d + m / 60.0 + sec / 3600.0);
            } catch { return double.NaN; }
        }

        private static ushort[] ReadPixelData(BinaryReader reader, int pixelCount, int bitpix, double bzero, double bscale) {
            ushort[] data = new ushort[pixelCount];
            switch (bitpix) {
                case 16: {
                    byte[] raw = reader.ReadBytes(pixelCount * 2);
                    for (int i = 0; i < pixelCount; i++) {
                        short val = (short)((raw[i * 2] << 8) | raw[i * 2 + 1]);
                        data[i] = (ushort)Math.Max(0, Math.Min(65535, val * bscale + bzero));
                    }
                    break;
                }
                case -32: {
                    byte[] raw = reader.ReadBytes(pixelCount * 4);
                    for (int i = 0; i < pixelCount; i++) {
                        byte[] fb = { raw[i * 4 + 3], raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4] };
                        float val = BitConverter.ToSingle(fb, 0);
                        double phys = val * bscale + bzero;
                        data[i] = phys <= 1.5 ? (ushort)(Math.Max(0, Math.Min(1.0, phys)) * 65535.0)
                            : (ushort)Math.Max(0, Math.Min(65535, phys));
                    }
                    break;
                }
                default: throw new Exception($"Unsupported BITPIX: {bitpix}");
            }
            return data;
        }

        private static FitsImage DebayerImage(ushort[] raw, int w, int h, string pattern) {
            ushort[] r = new ushort[w * h], g = new ushort[w * h], b = new ushort[w * h];
            int rRow, rCol, bRow, bCol;
            switch (pattern) {
                case "RGGB": rRow = 0; rCol = 0; bRow = 1; bCol = 1; break;
                case "BGGR": rRow = 1; rCol = 1; bRow = 0; bCol = 0; break;
                case "GRBG": rRow = 0; rCol = 1; bRow = 1; bCol = 0; break;
                case "GBRG": rRow = 1; rCol = 0; bRow = 0; bCol = 1; break;
                default: rRow = 0; rCol = 0; bRow = 1; bCol = 1; break;
            }
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int idx = y * w + x;
                    int ry = Math.Min((y & ~1) + rRow, h - 1), rx = Math.Min((x & ~1) + rCol, w - 1);
                    int by = Math.Min((y & ~1) + bRow, h - 1), bx = Math.Min((x & ~1) + bCol, w - 1);
                    r[idx] = raw[ry * w + rx]; b[idx] = raw[by * w + bx];
                    int g1y = Math.Min((y & ~1) + rRow, h - 1), g1x = Math.Min((x & ~1) + bCol, w - 1);
                    int g2y = Math.Min((y & ~1) + bRow, h - 1), g2x = Math.Min((x & ~1) + rCol, w - 1);
                    g[idx] = (ushort)((raw[g1y * w + g1x] + raw[g2y * w + g2x]) / 2);
                }
            }
            return new FitsImage(r, g, b, w, h);
        }
    }

    public class FitsImage {
        public ushort[] R { get; } public ushort[] G { get; } public ushort[] B { get; }
        public int Width { get; } public int Height { get; }
        public string ObjectName { get; set; } = "";
        public double RaHours { get; set; } = double.NaN;
        public double DecDeg { get; set; } = double.NaN;
        public double FocalLength { get; set; } = double.NaN;
        public double PixelSizeUm { get; set; } = double.NaN;
        public bool HasCoordinates => !double.IsNaN(RaHours) && !double.IsNaN(DecDeg);
        public bool HasFocalLength => !double.IsNaN(FocalLength) && FocalLength > 0;
        public FitsImage(ushort[] r, ushort[] g, ushort[] b, int width, int height) {
            R = r; G = g; B = b; Width = width; Height = height;
        }
    }
}
