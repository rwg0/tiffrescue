using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BitMiracle.LibTiff.Classic;
using BitMiracle.LibTiff.Classic.Internal;
using ComponentAce.Compression.Libs.zlib;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace TiffRescue
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("This utility may be able to rescue image data from compressed TIF files");
                Console.WriteLine("that are missing their metadata section. The file must be single channel");
                Console.WriteLine("(ie MONO or RAW), and be 16 bit per pixel and saved with DEFLATE compression.");
                Console.WriteLine("You need to know the width and height of the image.");
                Console.WriteLine();



                Console.WriteLine("Usage: TiffRescue <WIDTH>x<HEIGHT> Filename.tif");
                Console.WriteLine("");
                Console.WriteLine("ie: TiffRescue 640x480 broken.tif");
                Console.WriteLine();
                Console.WriteLine("The bit depth should be 8 or 16.");


                return;
            }

            var format = args[0];
            var bits = format.ToLower().Split('x');
            var width = int.Parse(bits[0]);
            var height = int.Parse(bits[1]);
            var bpp = 2;

            var path = args[1];
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Environment.CurrentDirectory, path);

            Console.WriteLine($"Reading from {path} as {width}x{height}x{bpp*8} bits per pixel");


            var sourceTif = path;

            //var goodtiff = Tiff.Open(sourceTif, "r");

            //var data = new byte[width * height * bpp];


            Tiff badtif = new Tiff();


            var f = File.OpenRead(sourceTif);
            f.Seek(4, SeekOrigin.Begin);
            var offset = new byte[4];
            f.Read(offset, 0, 4);
            var ifdOffset = BitConverter.ToInt32(offset, 0);
            var len = ifdOffset - 8;

            //if (ifdOffset != f.Length)
            //{
            //    Console.WriteLine("This tool cannot rescue the TIFF file specified, sorry.");
            //    return;
            //}

            var data = new byte[len];
            f.Read(data, 0, data.Length);
            f.Close();

            badtif.m_rawdata = data;
            badtif.m_rawcc = data.Length;
            DeflateCodec dc = new DeflateCodec(badtif, Compression.DEFLATE, "Deflate");
            dc.Init();


            byte[] buffer = new byte[width * height * bpp];
            dc.PreDecode(0);

            int writePos = 0;
            for (int y = 0; y < height; y++)
            {
                if (!dc.DecodeRow(buffer, writePos, buffer.Length - writePos, 0))
                {
                    Console.WriteLine($"Failed to decode row {y}, going to write out the data we have.");
                    break;
                }
                writePos = dc.m_stream.next_out_index;
                dc.m_stream.inflateInit();
            }

            int j = 0;
            for (int y = 0; y < height; y++)
            {
                ushort v = 0;

                for (int x = 0; x < width; x++)
                {
                    ushort cur = (ushort)(buffer[j] + 256 * buffer[j + 1]);
                    v = (ushort)((v + cur)%65536);
                    buffer[j] = (byte)(v % 256);
                    buffer[j + 1] = (byte)(v / 256);
                    j += 2;
                }
            }

            var bm = new Bitmap(width, height, PixelFormat.Format16bppGrayScale);

            var bd = bm.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format16bppGrayScale);

            Marshal.Copy(buffer, 0, bd.Scan0, buffer.Length);

            bm.UnlockBits(bd);

            SaveBmp(bm, Path.ChangeExtension(sourceTif, ".fixed.tif"));

           
        }

        private static void SaveBmp(Bitmap bmp, string path)
        {
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);

            BitmapData bitmapData = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);

            var pixelFormats = ConvertBmpPixelFormat(bmp.PixelFormat);

            BitmapSource source = BitmapSource.Create(bmp.Width,
                                                      bmp.Height,
                                                      bmp.HorizontalResolution,
                                                      bmp.VerticalResolution,
                                                      pixelFormats,
                                                      null,
                                                      bitmapData.Scan0,
                                                      bitmapData.Stride * bmp.Height,
                                                      bitmapData.Stride);

            bmp.UnlockBits(bitmapData);


            FileStream stream = new FileStream(path, FileMode.Create);

            TiffBitmapEncoder encoder = new TiffBitmapEncoder();

            encoder.Compression = TiffCompressOption.Zip;
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);

            stream.Close();
        }

        private static System.Windows.Media.PixelFormat ConvertBmpPixelFormat(System.Drawing.Imaging.PixelFormat pixelformat)
        {
            System.Windows.Media.PixelFormat pixelFormats = System.Windows.Media.PixelFormats.Default;

            switch (pixelformat)
            {
                case System.Drawing.Imaging.PixelFormat.Format32bppArgb:
                    pixelFormats = PixelFormats.Bgr32;
                    break;

                case System.Drawing.Imaging.PixelFormat.Format8bppIndexed:
                    pixelFormats = PixelFormats.Gray8;
                    break;

                case System.Drawing.Imaging.PixelFormat.Format16bppGrayScale:
                    pixelFormats = PixelFormats.Gray16;
                    break;
            }

            return pixelFormats;
        }
    }
}
