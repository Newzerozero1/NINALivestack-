using System;
using System.IO;
using System.Text;

namespace NinaLiveStack.Engine {

    public static class FitsWriter {

        /// <summary>
        /// Write a 32-bit float RGB FITS file from normalized [0,1] float arrays.
        /// FITS standard: NAXIS3=3, BITPIX=-32, channels stored as planes R,G,B.
        /// </summary>
        public static void Write32BitFits(string path, float[] r, float[] g, float[] b,
                                           int width, int height) {
            int pixelCount = width * height;
            if (r.Length != pixelCount || g.Length != pixelCount || b.Length != pixelCount)
                throw new ArgumentException("Array lengths must match width*height");

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);

            // Write FITS header (must be in 2880-byte blocks)
            var header = new StringBuilder();
            AddCard(header, "SIMPLE", "T", "file conforms to FITS standard");
            AddCard(header, "BITPIX", "-32", "32-bit IEEE floating point");
            AddCard(header, "NAXIS", "3", "number of axes");
            AddCard(header, "NAXIS1", width.ToString(), "width");
            AddCard(header, "NAXIS2", height.ToString(), "height");
            AddCard(header, "NAXIS3", "3", "R,G,B planes");
            AddCard(header, "BSCALE", "1.0", "");
            AddCard(header, "BZERO", "0.0", "");
            AddCardString(header, "CREATOR", "NinaLiveStack", "");
            AddCardString(header, "DATE", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"), "");
            header.Append("END".PadRight(80));

            // Pad header to multiple of 2880 bytes
            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            int headerBlocks = (headerBytes.Length + 2879) / 2880;
            byte[] paddedHeader = new byte[headerBlocks * 2880];
            Array.Copy(headerBytes, paddedHeader, headerBytes.Length);
            for (int i = headerBytes.Length; i < paddedHeader.Length; i++)
                paddedHeader[i] = 0x20; // space padding
            writer.Write(paddedHeader);

            // Write data: R plane, G plane, B plane
            // FITS stores rows bottom-to-top, big-endian
            byte[] pixelBuf = new byte[4];
            WriteChannel(writer, r, width, height, pixelBuf);
            WriteChannel(writer, g, width, height, pixelBuf);
            WriteChannel(writer, b, width, height, pixelBuf);

            // Pad data to 2880-byte boundary
            long dataLen = (long)pixelCount * 3 * 4;
            int dataBlocks = (int)((dataLen + 2879) / 2880);
            long paddedLen = (long)dataBlocks * 2880;
            long remaining = paddedLen - dataLen;
            if (remaining > 0) writer.Write(new byte[remaining]);
        }

        private static void WriteChannel(BinaryWriter writer, float[] data, int w, int h, byte[] buf) {
            // FITS: bottom row first
            for (int y = h - 1; y >= 0; y--) {
                int rowStart = y * w;
                for (int x = 0; x < w; x++) {
                    // Big-endian float
                    byte[] raw = BitConverter.GetBytes(data[rowStart + x]);
                    buf[0] = raw[3]; buf[1] = raw[2]; buf[2] = raw[1]; buf[3] = raw[0];
                    writer.Write(buf);
                }
            }
        }

        private static void AddCard(StringBuilder sb, string key, string value, string comment) {
            string card = $"{key,-8}= {value,20}";
            if (!string.IsNullOrEmpty(comment)) card += $" / {comment}";
            sb.Append(card.PadRight(80).Substring(0, 80));
        }

        private static void AddCardString(StringBuilder sb, string key, string value, string comment) {
            string card = $"{key,-8}= '{value}'";
            if (!string.IsNullOrEmpty(comment)) card += $" / {comment}";
            sb.Append(card.PadRight(80).Substring(0, 80));
        }
    }
}
