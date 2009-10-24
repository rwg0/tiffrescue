﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Collections;

using BitMiracle.LibTiff;

namespace BitMiracle.Tiff2Pdf
{
    /*
     * This is used to sort a T2P_PAGE array of page structures
     * by page number.
     */
    public class cmp_t2p_page : IComparer
    {
        int IComparer.Compare(object x, object y)
        {
            T2P_PAGE e1 = x as T2P_PAGE;
            T2P_PAGE e2 = y as T2P_PAGE;

            return e1.page_number - e2.page_number;
        }
    }

    /// <summary>
    /// This is the context of a function to generate PDF from a TIFF.
    /// </summary>
    class T2P
    {
        public bool m_error;

        public t2p_compress_t m_pdf_defaultcompression;
        public ushort m_pdf_defaultcompressionquality;

        public bool m_pdf_nopassthrough;
        public bool m_pdf_colorspace_invert;
        public bool m_pdf_fitwindow;
        public bool m_pdf_image_interpolate; /* false (default) : do not interpolate, true : interpolate */

        public bool m_pdf_centimeters;
        public bool m_pdf_overrideres;
        public bool m_pdf_overridepagesize;

        public float m_pdf_defaultxres;
        public float m_pdf_defaultyres;

        public float m_pdf_defaultpagewidth;
        public float m_pdf_defaultpagelength;

        public byte[] m_pdf_datetime;
        public byte[] m_pdf_creator;
        public byte[] m_pdf_author;
        public byte[] m_pdf_title;
        public byte[] m_pdf_subject;
        public byte[] m_pdf_keywords;

        /* fields for custom read/write procedures */
        public Stream m_outputfile;
        public bool m_outputdisable;
        public int m_outputwritten;

        public MyErrorHandler m_errorHandler;
        public MyStream m_stream;

        private T2P_PAGE[] m_tiff_pages;
        private T2P_TILES[] m_tiff_tiles;
        private ushort m_tiff_pagecount;
        private COMPRESSION m_tiff_compression;
        private PHOTOMETRIC m_tiff_photometric;
        private FILLORDER m_tiff_fillorder;
        private ushort m_tiff_bitspersample;
        private ushort m_tiff_samplesperpixel;
        private PLANARCONFIG m_tiff_planar;
        private int m_tiff_width;
        private int m_tiff_length;
        private float m_tiff_xres;
        private float m_tiff_yres;
        private ORIENTATION m_tiff_orientation;
        private int m_tiff_datasize;
        private RESUNIT m_tiff_resunit;
        
        private float m_pdf_xres;
        private float m_pdf_yres;
        
        private float m_pdf_pagewidth;
        private float m_pdf_pagelength;
        private float m_pdf_imagewidth;
        private float m_pdf_imagelength;
        private T2P_BOX m_pdf_mediabox = new T2P_BOX();
        private T2P_BOX m_pdf_imagebox = new T2P_BOX();
        private ushort m_pdf_majorversion;
        private ushort m_pdf_minorversion;
        private int m_pdf_catalog;
        private int m_pdf_pages;
        private int m_pdf_info;
        private int m_pdf_palettecs;
        
        private int m_pdf_startxref;
        private byte[] m_pdf_fileid;
        
        private t2p_cs_t m_pdf_colorspace;
        
        private bool m_pdf_switchdecode;
        private int m_pdf_palettesize;
        private byte[] m_pdf_palette;
        private int[] m_pdf_labrange = new int[4];
        
        private t2p_compress_t m_pdf_compression;
        
        private t2p_transcode_t m_pdf_transcode;
        private t2p_sample_t m_pdf_sample;
        private int[] m_pdf_xrefoffsets;
        private int m_pdf_xrefcount;
        private ushort m_pdf_page;
        private float[] m_tiff_whitechromaticities = new float[2];
        private float[] m_tiff_primarychromaticities = new float[6];
        private float[][] m_tiff_transferfunction = new float[3][];
        
        private ushort m_tiff_transferfunctioncount;
        private int m_pdf_icccs;
        private int m_tiff_iccprofilelength;
        private byte[] m_tiff_iccprofile;

        private Tiff m_output;

        public T2P()
        {
            m_errorHandler = new MyErrorHandler();
            Tiff.SetErrorHandler(m_errorHandler);

            m_stream = new MyStream();
            m_pdf_majorversion = 1;
            m_pdf_minorversion = 1;
            m_pdf_defaultxres = 300.0f;
            m_pdf_defaultyres = 300.0f;
            m_pdf_defaultpagewidth = 612.0f;
            m_pdf_defaultpagelength = 792.0f;
            m_pdf_xrefcount = 3; /* Catalog, Info, Pages */
        }
        
        /*
        This function validates the values of a T2P context struct pointer
        before calling write_pdf with it.
        */
        public void validate()
        {
            if (m_pdf_defaultcompression == t2p_compress_t.T2P_COMPRESS_JPEG)
            {
                if (m_pdf_defaultcompressionquality > 100 || m_pdf_defaultcompressionquality < 1)
                    m_pdf_defaultcompressionquality = 0;
            }
            
            if (m_pdf_defaultcompression == t2p_compress_t.T2P_COMPRESS_ZIP)
            {
                int m = m_pdf_defaultcompressionquality % 100;
                if (m_pdf_defaultcompressionquality / 100 > 9 || (m > 1 && m < 10) || m > 15)
                    m_pdf_defaultcompressionquality = 0;

                if (m_pdf_defaultcompressionquality % 100 != 0)
                {
                    m_pdf_defaultcompressionquality /= 100;
                    m_pdf_defaultcompressionquality *= 100;
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "PNG Group predictor differencing not implemented, assuming compression quality %u", m_pdf_defaultcompressionquality);
                }
                
                m_pdf_defaultcompressionquality %= 100;
                if (m_pdf_minorversion < 2)
                    m_pdf_minorversion = 2;
            }
        }

        /*

        This function writes a PDF to a file given a pointer to a TIFF.

        The idea with using a Tiff as output for a PDF file is that the file 
        can be created with ClientOpen for memory-mapped use within the TIFF 
        library, and WriteEncodedStrip can be used to write compressed data to 
        the output.  The output is not actually a TIFF file, it is a PDF file.  

        This function uses only writeToFile and WriteEncodedStrip to write to 
        the output TIFF file.  When libtiff would otherwise be writing data to the 
        output file, the write procedure of the TIFF structure is replaced with an 
        empty implementation.

        The first argument to the function is an initialized and validated T2P 
        context struct pointer.

        The second argument to the function is the Tiff that is the input that has 
        been opened for reading and no other functions have been called upon it.

        The third argument to the function is the Tiff that is the output that has 
        been opened for writing.  It has to be opened so that it hasn't written any 
        data to the output.  If the output is seekable then it's OK to seek to the 
        beginning of the file.  The function only writes to the output PDF and does 
        not seek.  See the example usage in the main() function.

        Tiff output = Open("output.pdf", "w");
        assert(output != null);

        if(output.tif_seekproc != null){
        t2pSeekFile(output, (toff_t) 0, SEEK_SET);
        }

        This function returns the file size of the output PDF file.  On error it 
        returns zero and the t2p.m_error variable is set to true.

        After this function completes, delete t2p, TIFFClose on input, 
        and TIFFClose on output.
        */
        public int write_pdf(Tiff input, Tiff output)
        {
            read_tiff_init(input);
            if (m_error)
                return 0;

            m_pdf_xrefoffsets = new int [m_pdf_xrefcount];
            m_output = output;
            m_pdf_xrefcount = 0;
            m_pdf_catalog = 1;
            m_pdf_info = 2;
            m_pdf_pages = 3;

            int written = write_pdf_header();
            m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
            m_pdf_catalog = m_pdf_xrefcount;
            written += write_pdf_obj_start(m_pdf_xrefcount);
            written += write_pdf_catalog();
            written += write_pdf_obj_end();
            m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
            m_pdf_info = m_pdf_xrefcount;
            written += write_pdf_obj_start(m_pdf_xrefcount);
            written += write_pdf_info(input);
            written += write_pdf_obj_end();
            m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
            m_pdf_pages = m_pdf_xrefcount;
            written += write_pdf_obj_start(m_pdf_xrefcount);
            written += write_pdf_pages();
            written += write_pdf_obj_end();
            for (m_pdf_page = 0; m_pdf_page < m_tiff_pagecount; m_pdf_page++)
            {
                read_tiff_data(input);
                if (m_error)
                    return 0;

                m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                written += write_pdf_obj_start(m_pdf_xrefcount);
                written += write_pdf_page(m_pdf_xrefcount);
                written += write_pdf_obj_end();
                m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                written += write_pdf_obj_start(m_pdf_xrefcount);
                written += write_pdf_stream_dict_start();
                written += write_pdf_stream_dict(0, m_pdf_xrefcount + 1);
                written += write_pdf_stream_dict_end();
                written += write_pdf_stream_start();
                int streamlen = written;
                written += write_pdf_page_content_stream();
                streamlen = written - streamlen;
                written += write_pdf_stream_end();
                written += write_pdf_obj_end();
                m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                written += write_pdf_obj_start(m_pdf_xrefcount);
                written += write_pdf_stream_length(streamlen);
                written += write_pdf_obj_end();
                
                if (m_tiff_transferfunctioncount != 0)
                {
                    m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                    written += write_pdf_obj_start(m_pdf_xrefcount);
                    written += write_pdf_transfer();
                    written += write_pdf_obj_end();
                    
                    for (ushort i = 0; i < m_tiff_transferfunctioncount; i++)
                    {
                        m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                        written += write_pdf_obj_start(m_pdf_xrefcount);
                        written += write_pdf_stream_dict_start();
                        written += write_pdf_transfer_dict();
                        written += write_pdf_stream_dict_end();
                        written += write_pdf_stream_start();
                        streamlen = written;
                        written += write_pdf_transfer_stream(i);
                        streamlen = written - streamlen;
                        written += write_pdf_stream_end();
                        written += write_pdf_obj_end();
                    }
                }

                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_PALETTE) != 0)
                {
                    m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                    m_pdf_palettecs = m_pdf_xrefcount;
                    written += write_pdf_obj_start(m_pdf_xrefcount);
                    written += write_pdf_stream_dict_start();
                    written += write_pdf_stream_dict(m_pdf_palettesize, 0);
                    written += write_pdf_stream_dict_end();
                    written += write_pdf_stream_start();
                    streamlen = written;
                    written += write_pdf_xobject_palettecs_stream();
                    streamlen = written - streamlen;
                    written += write_pdf_stream_end();
                    written += write_pdf_obj_end();
                }

                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_ICCBASED) != 0)
                {
                    m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                    m_pdf_icccs = m_pdf_xrefcount;
                    written += write_pdf_obj_start(m_pdf_xrefcount);
                    written += write_pdf_stream_dict_start();
                    written += write_pdf_xobject_icccs_dict();
                    written += write_pdf_stream_dict_end();
                    written += write_pdf_stream_start();
                    streamlen = written;
                    written += write_pdf_xobject_icccs_stream();
                    streamlen = written - streamlen;
                    written += write_pdf_stream_end();
                    written += write_pdf_obj_end();
                }

                if (m_tiff_tiles[m_pdf_page].tiles_tilecount != 0)
                {
                    for (int i2 = 0; i2 < m_tiff_tiles[m_pdf_page].tiles_tilecount; i2++)
                    {
                        m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                        written += write_pdf_obj_start(m_pdf_xrefcount);
                        written += write_pdf_stream_dict_start();
                        written += write_pdf_xobject_stream_dict(i2 + 1);
                        written += write_pdf_stream_dict_end();
                        written += write_pdf_stream_start();
                        streamlen = written;
                        read_tiff_size_tile(input, i2);
                        written += readwrite_pdf_image_tile(input, i2);
                        write_advance_directory();
                        if (m_error)
                            return 0;

                        streamlen = written - streamlen;
                        written += write_pdf_stream_end();
                        written += write_pdf_obj_end();
                        m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                        written += write_pdf_obj_start(m_pdf_xrefcount);
                        written += write_pdf_stream_length(streamlen);
                        written += write_pdf_obj_end();
                    }
                }
                else
                {
                    m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                    written += write_pdf_obj_start(m_pdf_xrefcount);
                    written += write_pdf_stream_dict_start();
                    written += write_pdf_xobject_stream_dict(0);
                    written += write_pdf_stream_dict_end();
                    written += write_pdf_stream_start();
                    streamlen = written;
                    read_tiff_size(input);
                    written += readwrite_pdf_image(input);
                    write_advance_directory();
                    
                    if (m_error)
                        return 0;

                    streamlen = written - streamlen;
                    written += write_pdf_stream_end();
                    written += write_pdf_obj_end();
                    m_pdf_xrefoffsets[m_pdf_xrefcount++] = written;
                    written += write_pdf_obj_start(m_pdf_xrefcount);
                    written += write_pdf_stream_length(streamlen);
                    written += write_pdf_obj_end();
                }
            }

            m_pdf_startxref = written;
            written += write_pdf_xreftable();
            written += write_pdf_trailer();
            disable(output);
            return written;
        }

        /*
        This function scans the input TIFF file for pages.  It attempts
        to determine which IFD's of the TIFF file contain image document
        pages.  For each, it gathers some information that has to do
        with the output of the PDF document as a whole.  
        */
        private void read_tiff_init(Tiff input)
        {
            ushort directorycount = input.NumberOfDirectories();
            m_tiff_pages = new T2P_PAGE [directorycount];
            for (int p = 0; p < directorycount; p++)
                m_tiff_pages[p] = new T2P_PAGE();

            m_tiff_tiles = new T2P_TILES [directorycount];
            FieldValue[] result = null;

            for (short i = 0; i < directorycount; i++)
            {
                uint subfiletype = 0;

                if (!input.SetDirectory(i))
                {
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Can't set directory %u of input file %s", i, input.FileName());
                    return ;
                }

                result = input.GetField(TIFFTAG.TIFFTAG_PAGENUMBER);
                if (result != null)
                {
                    ushort pagen = result[0].ToUShort();
                    ushort paged = result[1].ToUShort();

                    if ((pagen > paged) && (paged != 0))
                        m_tiff_pages[m_tiff_pagecount].page_number = paged;
                    else
                        m_tiff_pages[m_tiff_pagecount].page_number = pagen;
                }
                else
                {
                    result = input.GetField(TIFFTAG.TIFFTAG_SUBFILETYPE);
                    if (result != null)
                    {
                        subfiletype = result[0].ToUInt();
                        if ((((FILETYPE)subfiletype & FILETYPE.FILETYPE_PAGE) == 0) && (subfiletype != 0))
                            continue;
                    }
                    else
                    {
                        result = input.GetField(TIFFTAG.TIFFTAG_OSUBFILETYPE);
                        if (result != null)
                        {
                            subfiletype = result[0].ToUInt();
                            if (((OFILETYPE)subfiletype != OFILETYPE.OFILETYPE_IMAGE) && ((OFILETYPE)subfiletype != OFILETYPE.OFILETYPE_PAGE) && (subfiletype != 0))
                                continue;
                        }
                    }

                    m_tiff_pages[m_tiff_pagecount].page_number = m_tiff_pagecount;
                }

                m_tiff_pages[m_tiff_pagecount].page_directory = i;

                if (input.IsTiled())
                    m_tiff_pages[m_tiff_pagecount].page_tilecount = input.NumberOfTiles();
                
                m_tiff_pagecount++;
            }

            IComparer myComparer = new cmp_t2p_page();
            Array.Sort(m_tiff_pages, myComparer);

            for (ushort i = 0; i < m_tiff_pagecount; i++)
            {
                m_pdf_xrefcount += 5;
                input.SetDirectory(m_tiff_pages[i].page_directory);

                result = input.GetField(TIFFTAG.TIFFTAG_PHOTOMETRIC);
                if ((result != null && ((PHOTOMETRIC)result[0].ToInt() == PHOTOMETRIC.PHOTOMETRIC_PALETTE)) || input.GetField(TIFFTAG.TIFFTAG_INDEXED) != null)
                {
                    m_tiff_pages[i].page_extra++;
                    m_pdf_xrefcount++;
                }

                result = input.GetField(TIFFTAG.TIFFTAG_COMPRESSION);
                if (result != null)
                {
                    COMPRESSION xuint16 = (COMPRESSION)result[0].ToInt();
                    if ((xuint16 == COMPRESSION.COMPRESSION_DEFLATE || xuint16 == COMPRESSION.COMPRESSION_ADOBE_DEFLATE) 
                        && ((m_tiff_pages[i].page_tilecount != 0) || input.NumberOfStrips() == 1) 
                        && !m_pdf_nopassthrough)
                    {
                        if (m_pdf_minorversion < 2)
                            m_pdf_minorversion = 2;
                    }
                }

                result = input.GetField(TIFFTAG.TIFFTAG_TRANSFERFUNCTION);
                if (result != null)
                {
                    m_tiff_transferfunction[0] = result[0].ToFloatArray();
                    m_tiff_transferfunction[1] = result[1].ToFloatArray();
                    m_tiff_transferfunction[2] = result[2].ToFloatArray();

                    if (m_tiff_transferfunction[1] != m_tiff_transferfunction[0])
                    {
                        m_tiff_transferfunctioncount = 3;
                        m_tiff_pages[i].page_extra += 4;
                        m_pdf_xrefcount += 4;
                    }
                    else
                    {
                        m_tiff_transferfunctioncount = 1;
                        m_tiff_pages[i].page_extra += 2;
                        m_pdf_xrefcount += 2;
                    }

                    if (m_pdf_minorversion < 2)
                        m_pdf_minorversion = 2;
                }
                else
                {
                    m_tiff_transferfunctioncount = 0;
                }

                result = input.GetField(TIFFTAG.TIFFTAG_ICCPROFILE);
                if (result != null)
                {
                    m_tiff_iccprofilelength = result[0].ToInt();
                    m_tiff_iccprofile = result[1].ToByteArray();

                    m_tiff_pages[i].page_extra++;
                    m_pdf_xrefcount++;
                    if (m_pdf_minorversion < 3)
                        m_pdf_minorversion = 3;
                }

                m_tiff_tiles[i].tiles_tilecount = m_tiff_pages[i].page_tilecount;

                result = input.GetField(TIFFTAG.TIFFTAG_PLANARCONFIG);
                if (result != null && ((PLANARCONFIG)result[0].ToShort() == PLANARCONFIG.PLANARCONFIG_SEPARATE))
                {
                    result = input.GetField(TIFFTAG.TIFFTAG_SAMPLESPERPIXEL);
                    int xuint16 = result[0].ToInt();
                    m_tiff_tiles[i].tiles_tilecount /= xuint16;
                }
                
                if (m_tiff_tiles[i].tiles_tilecount > 0)
                {
                    m_pdf_xrefcount += (m_tiff_tiles[i].tiles_tilecount - 1) * 2;
                    result = input.GetField(TIFFTAG.TIFFTAG_TILEWIDTH);
                    m_tiff_tiles[i].tiles_tilewidth = result[0].ToInt();

                    input.GetField(TIFFTAG.TIFFTAG_TILELENGTH);
                    m_tiff_tiles[i].tiles_tilelength = result[0].ToInt();

                    m_tiff_tiles[i].tiles_tiles = new T2P_TILE [m_tiff_tiles[i].tiles_tilecount];
                }
            }
        }

        /*
        This function sets the input directory to the directory of a given
        page and determines information about the image.  It checks
        the image characteristics to determine if it is possible to convert
        the image data into a page of PDF output, setting values of the T2P
        struct for this page.  It determines what color space is used in
        the output PDF to represent the image.

        It determines if the image can be converted as raw data without
        requiring transcoding of the image data.
        */
        private void read_tiff_data(Tiff input)
        {
            m_pdf_transcode = t2p_transcode_t.T2P_TRANSCODE_ENCODE;
            m_pdf_sample = t2p_sample_t.T2P_SAMPLE_NOTHING;
            m_pdf_switchdecode = m_pdf_colorspace_invert;

            input.SetDirectory(m_tiff_pages[m_pdf_page].page_directory);

            FieldValue[] result = input.GetField(TIFFTAG.TIFFTAG_IMAGEWIDTH);
            m_tiff_width = result[0].ToInt();
            if (m_tiff_width == 0)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with zero width", input.FileName());
                m_error = true;
                return ;
            }

            result = input.GetField(TIFFTAG.TIFFTAG_IMAGELENGTH);
            m_tiff_length = result[0].ToInt();
            if (m_tiff_length == 0)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with zero length", input.FileName());
                m_error = true;
                return ;
            }

            result = input.GetField(TIFFTAG.TIFFTAG_COMPRESSION);
            if (result == null)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with no compression tag", input.FileName());
                m_error = true;
                return;
            }
            else
                m_tiff_compression = (COMPRESSION)result[0].ToInt();

            if (!input.IsCodecConfigured(m_tiff_compression))
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with compression type %u:  not configured", input.FileName(), m_tiff_compression);
                m_error = true;
                return ;

            }

            result = input.GetFieldDefaulted(TIFFTAG.TIFFTAG_BITSPERSAMPLE);
            m_tiff_bitspersample = result[0].ToUShort();

            switch (m_tiff_bitspersample)
            {
                case 1:
                case 2:
                case 4:
                case 8:
                    break;
                case 0:
                    Tiff.Warning(Tiff2PdfConstants.TIFF2PDF_MODULE, "Image %s has 0 bits per sample, assuming 1", input.FileName());
                    m_tiff_bitspersample = 1;
                    break;
                default:
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with %u bits per sample", input.FileName(), m_tiff_bitspersample);
                    m_error = true;
                    return ;
            }

            result = input.GetFieldDefaulted(TIFFTAG.TIFFTAG_SAMPLESPERPIXEL);
            m_tiff_samplesperpixel = result[0].ToUShort();
            if (m_tiff_samplesperpixel > 4)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with %u samples per pixel", input.FileName(), m_tiff_samplesperpixel);
                m_error = true;
                return ;
            }

            if (m_tiff_samplesperpixel == 0)
            {
                Tiff.Warning(Tiff2PdfConstants.TIFF2PDF_MODULE, "Image %s has 0 samples per pixel, assuming 1", input.FileName());
                m_tiff_samplesperpixel = 1;
            }

            result = input.GetField(TIFFTAG.TIFFTAG_SAMPLEFORMAT);
            if (result != null)
            {
                SAMPLEFORMAT f = (SAMPLEFORMAT)result[0].ToByte();
                switch (f)
                {
                    case 0:
                    case SAMPLEFORMAT.SAMPLEFORMAT_UINT:
                    case SAMPLEFORMAT.SAMPLEFORMAT_VOID:
                        break;

                    default:
                        Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with sample format %u", input.FileName(), f);
                        m_error = true;
                        return;
                }
            }

            result = input.GetFieldDefaulted(TIFFTAG.TIFFTAG_FILLORDER);
            m_tiff_fillorder = (FILLORDER)result[0].ToByte();

            result = input.GetField(TIFFTAG.TIFFTAG_PHOTOMETRIC);
            if (result == null)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with no photometric interpretation tag", input.FileName());
                m_error = true;
                return;
            }
            else
                m_tiff_photometric = (PHOTOMETRIC)result[0].ToInt();

            ushort[] r;
            ushort[] g;
            ushort[] b;
            ushort[] a;
            bool photometric_palette;
            bool photometric_palette_cmyk;

            switch (m_tiff_photometric)
            {
                case PHOTOMETRIC.PHOTOMETRIC_MINISWHITE:
                case PHOTOMETRIC.PHOTOMETRIC_MINISBLACK:
                    if (m_tiff_bitspersample == 1)
                    {
                        m_pdf_colorspace = t2p_cs_t.T2P_CS_BILEVEL;
                        if (m_tiff_photometric == PHOTOMETRIC.PHOTOMETRIC_MINISWHITE)
                            m_pdf_switchdecode ^= true;
                    }
                    else
                    {
                        m_pdf_colorspace = t2p_cs_t.T2P_CS_GRAY;
                        if (m_tiff_photometric == PHOTOMETRIC.PHOTOMETRIC_MINISWHITE)
                            m_pdf_switchdecode ^= true;
                    }
                    break;
               
                case PHOTOMETRIC.PHOTOMETRIC_RGB:
                case PHOTOMETRIC.PHOTOMETRIC_PALETTE:
                    photometric_palette = (m_tiff_photometric == PHOTOMETRIC.PHOTOMETRIC_PALETTE);
                    if (!photometric_palette)
                    {
                        m_pdf_colorspace = t2p_cs_t.T2P_CS_RGB;
                        if (m_tiff_samplesperpixel == 3)
                            break;

                        result = input.GetField(TIFFTAG.TIFFTAG_INDEXED);
                        if (result != null)
                        {
                            if (result[0].ToInt() == 1)
                                photometric_palette = true;
                        }
                    }

                    if (!photometric_palette)
                    {
                        if (m_tiff_samplesperpixel > 3)
                        {
                            if (m_tiff_samplesperpixel == 4)
                            {
                                m_pdf_colorspace = t2p_cs_t.T2P_CS_RGB;

                                result = input.GetField(TIFFTAG.TIFFTAG_EXTRASAMPLES);
                                if (result != null && result[0].ToInt() == 1)
                                {
                                    byte[] xuint16p = result[1].ToByteArray();
                                    if ((EXTRASAMPLE)xuint16p[0] == EXTRASAMPLE.EXTRASAMPLE_ASSOCALPHA)
                                    {
                                        m_pdf_sample = t2p_sample_t.T2P_SAMPLE_RGBAA_TO_RGB;
                                        break;
                                    }

                                    if ((EXTRASAMPLE)xuint16p[0] == EXTRASAMPLE.EXTRASAMPLE_UNASSALPHA)
                                    {
                                        m_pdf_sample = t2p_sample_t.T2P_SAMPLE_RGBA_TO_RGB;
                                        break;
                                    }
                                    
                                    Tiff.Warning(Tiff2PdfConstants.TIFF2PDF_MODULE, "RGB image %s has 4 samples per pixel, assuming RGBA", input.FileName());
                                    break;
                                }

                                m_pdf_colorspace = t2p_cs_t.T2P_CS_CMYK;
                                m_pdf_switchdecode ^= true;
                                Tiff.Warning(Tiff2PdfConstants.TIFF2PDF_MODULE, "RGB image %s has 4 samples per pixel, assuming inverse CMYK", input.FileName());
                                break;
                            }
                            else
                            {
                                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for RGB image %s with %u samples per pixel", input.FileName(), m_tiff_samplesperpixel);
                                m_error = true;
                                break;
                            }
                        }
                        else
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for RGB image %s with %u samples per pixel", input.FileName(), m_tiff_samplesperpixel);
                            m_error = true;
                            break;
                        }
                    }

                    if (photometric_palette)
                    {
                        if (m_tiff_samplesperpixel != 1)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for palettized image %s with not one sample per pixel", input.FileName());
                            m_error = true;
                            return ;
                        }

                        m_pdf_colorspace = (t2p_cs_t)(t2p_cs_t.T2P_CS_RGB | t2p_cs_t.T2P_CS_PALETTE);
                        m_pdf_palettesize = 0x0001 << m_tiff_bitspersample;

                        result = input.GetField(TIFFTAG.TIFFTAG_COLORMAP);
                        if (result == null)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Palletized image %s has no color map", input.FileName());
                            m_error = true;
                            return;
                        }
                        else
                        {
                            r = result[0].ToUShortArray();
                            g = result[1].ToUShortArray();
                            b = result[2].ToUShortArray();
                        }

                        m_pdf_palette = new byte [m_pdf_palettesize * 3];
                        for (int i = 0; i < m_pdf_palettesize; i++)
                        {
                            m_pdf_palette[i * 3] = (byte)(r[i] >> 8);
                            m_pdf_palette[i * 3 + 1] = (byte)(g[i] >> 8);
                            m_pdf_palette[i * 3 + 2] = (byte)(b[i] >> 8);
                        }

                        m_pdf_palettesize *= 3;
                    }
                    break;

                case PHOTOMETRIC.PHOTOMETRIC_SEPARATED:
                    photometric_palette_cmyk = false;
                    result = input.GetField(TIFFTAG.TIFFTAG_INDEXED);
                    if (result != null)
                    {
                        if (result[0].ToInt() == 1)
                            photometric_palette_cmyk = true;
                    }

                    if (!photometric_palette_cmyk)
                    {
                        result = input.GetField(TIFFTAG.TIFFTAG_INKSET);
                        if (result != null)
                        {
                            if ((INKSET)result[0].ToByte() != INKSET.INKSET_CMYK)
                            {
                                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s because its inkset is not CMYK", input.FileName());
                                m_error = true;
                                return ;
                            }
                        }
                        
                        if (m_tiff_samplesperpixel == 4)
                        {
                            m_pdf_colorspace = t2p_cs_t.T2P_CS_CMYK;
                        }
                        else
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s because it has %u samples per pixel", input.FileName(), m_tiff_samplesperpixel);
                            m_error = true;
                            return ;
                        }
                    }
                    else
                    {
                        if (m_tiff_samplesperpixel != 1)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for palletized CMYK image %s with not one sample per pixel", input.FileName());
                            m_error = true;
                            return ;
                        }
                        
                        m_pdf_colorspace = (t2p_cs_t)(t2p_cs_t.T2P_CS_CMYK | t2p_cs_t.T2P_CS_PALETTE);
                        m_pdf_palettesize = 0x0001 << m_tiff_bitspersample;
                        
                        result = input.GetField(TIFFTAG.TIFFTAG_COLORMAP);
                        if (result == null)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Palletized image %s has no color map", input.FileName());
                            m_error = true;
                            return;
                        }
                        else
                        {
                            r = result[0].ToUShortArray();
                            g = result[1].ToUShortArray();
                            b = result[2].ToUShortArray();
                            a = result[3].ToUShortArray();
                        }
                        
                        m_pdf_palette = new byte [m_pdf_palettesize * 4];
                        for (int i = 0; i < m_pdf_palettesize; i++)
                        {
                            m_pdf_palette[i * 4] = (byte)(r[i] >> 8);
                            m_pdf_palette[i * 4 + 1] = (byte)(g[i] >> 8);
                            m_pdf_palette[i * 4 + 2] = (byte)(b[i] >> 8);
                            m_pdf_palette[i * 4 + 3] = (byte)(a[i] >> 8);
                        }

                        m_pdf_palettesize *= 4;
                    }
                    break;
                
                case PHOTOMETRIC.PHOTOMETRIC_YCBCR:
                    m_pdf_colorspace = t2p_cs_t.T2P_CS_RGB;
                    if (m_tiff_samplesperpixel == 1)
                    {
                        m_pdf_colorspace = t2p_cs_t.T2P_CS_GRAY;
                        m_tiff_photometric = PHOTOMETRIC.PHOTOMETRIC_MINISBLACK;
                        break;
                    }

                    m_pdf_sample = t2p_sample_t.T2P_SAMPLE_YCBCR_TO_RGB;
                    if (m_pdf_defaultcompression == t2p_compress_t.T2P_COMPRESS_JPEG)
                        m_pdf_sample = t2p_sample_t.T2P_SAMPLE_NOTHING;

                    break;

                case PHOTOMETRIC.PHOTOMETRIC_CIELAB:
                    m_pdf_labrange[0] = -127;
                    m_pdf_labrange[1] = 127;
                    m_pdf_labrange[2] = -127;
                    m_pdf_labrange[3] = 127;
                    m_pdf_sample = t2p_sample_t.T2P_SAMPLE_LAB_SIGNED_TO_UNSIGNED;
                    m_pdf_colorspace = t2p_cs_t.T2P_CS_LAB;
                    break;

                case PHOTOMETRIC.PHOTOMETRIC_ICCLAB:
                    m_pdf_labrange[0] = 0;
                    m_pdf_labrange[1] = 255;
                    m_pdf_labrange[2] = 0;
                    m_pdf_labrange[3] = 255;
                    m_pdf_colorspace = t2p_cs_t.T2P_CS_LAB;
                    break;

                case PHOTOMETRIC.PHOTOMETRIC_ITULAB:
                    m_pdf_labrange[0] = -85;
                    m_pdf_labrange[1] = 85;
                    m_pdf_labrange[2] = -75;
                    m_pdf_labrange[3] = 124;
                    m_pdf_sample = t2p_sample_t.T2P_SAMPLE_LAB_SIGNED_TO_UNSIGNED;
                    m_pdf_colorspace = t2p_cs_t.T2P_CS_LAB;
                    break;

                case PHOTOMETRIC.PHOTOMETRIC_LOGL:
                case PHOTOMETRIC.PHOTOMETRIC_LOGLUV:
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with photometric interpretation LogL/LogLuv", input.FileName());
                    m_error = true;
                    return ;
                default:
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with photometric interpretation %u", input.FileName(), m_tiff_photometric);
                    m_error = true;
                    return ;
            }

            result = input.GetField(TIFFTAG.TIFFTAG_PLANARCONFIG);
            if (result != null)
            {
                m_tiff_planar = (PLANARCONFIG)result[0].ToShort();
                switch (m_tiff_planar)
                {
                    case 0:
                        Tiff.Warning(Tiff2PdfConstants.TIFF2PDF_MODULE, "Image %s has planar configuration 0, assuming 1", input.FileName());
                        m_tiff_planar = PLANARCONFIG.PLANARCONFIG_CONTIG;
                        break;

                    case PLANARCONFIG.PLANARCONFIG_CONTIG:
                        break;
                    
                    case PLANARCONFIG.PLANARCONFIG_SEPARATE:
                        m_pdf_sample = t2p_sample_t.T2P_SAMPLE_PLANAR_SEPARATE_TO_CONTIG;
                        if (m_tiff_bitspersample != 8)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with separated planar configuration and %u bits per sample", input.FileName(), m_tiff_bitspersample);
                            m_error = true;
                            return ;
                        }
                        break;
                    
                    default:
                        Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with planar configuration %u", input.FileName(), m_tiff_planar);
                        m_error = true;
                        return;
                }
            }

            result = input.GetFieldDefaulted(TIFFTAG.TIFFTAG_ORIENTATION);
            m_tiff_orientation = (ORIENTATION)result[0].ToByte();

            if (m_tiff_orientation > ORIENTATION.ORIENTATION_LEFTBOT)
            {
                Tiff.Warning(Tiff2PdfConstants.TIFF2PDF_MODULE, "Image %s has orientation %u, assuming 0", input.FileName(), m_tiff_orientation);
                m_tiff_orientation = 0;
            }

            result = input.GetField(TIFFTAG.TIFFTAG_XRESOLUTION);
            if (result == null)
                m_tiff_xres = 0.0f;
            else
                m_tiff_xres = result[0].ToFloat();

            result = input.GetField(TIFFTAG.TIFFTAG_YRESOLUTION);
            if (result == null)
                m_tiff_yres = 0.0f;
            else
                m_tiff_yres = result[0].ToFloat();

            result = input.GetFieldDefaulted(TIFFTAG.TIFFTAG_RESOLUTIONUNIT);
            m_tiff_resunit = (RESUNIT)result[0].ToByte();
            if (m_tiff_resunit == RESUNIT.RESUNIT_CENTIMETER)
            {
                m_tiff_xres *= 2.54F;
                m_tiff_yres *= 2.54F;
            }
            else if (m_tiff_resunit != RESUNIT.RESUNIT_INCH && m_pdf_centimeters)
            {
                m_tiff_xres *= 2.54F;
                m_tiff_yres *= 2.54F;
            }

            compose_pdf_page();

            m_pdf_transcode = t2p_transcode_t.T2P_TRANSCODE_ENCODE;
            if (!m_pdf_nopassthrough)
            {
                if (m_tiff_compression == COMPRESSION.COMPRESSION_CCITTFAX4)
                {
                    if (input.IsTiled() || (input.NumberOfStrips() == 1))
                    {
                        m_pdf_transcode = t2p_transcode_t.T2P_TRANSCODE_RAW;
                        m_pdf_compression = t2p_compress_t.T2P_COMPRESS_G4;
                    }
                }

                if (m_tiff_compression == COMPRESSION.COMPRESSION_ADOBE_DEFLATE || m_tiff_compression == COMPRESSION.COMPRESSION_DEFLATE)
                {
                    if (input.IsTiled() || (input.NumberOfStrips() == 1))
                    {
                        m_pdf_transcode = t2p_transcode_t.T2P_TRANSCODE_RAW;
                        m_pdf_compression = t2p_compress_t.T2P_COMPRESS_ZIP;
                    }
                }

                if (m_tiff_compression == COMPRESSION.COMPRESSION_JPEG)
                {
                    m_pdf_transcode = t2p_transcode_t.T2P_TRANSCODE_RAW;
                    m_pdf_compression = t2p_compress_t.T2P_COMPRESS_JPEG;
                }
            }

            if (m_pdf_transcode != t2p_transcode_t.T2P_TRANSCODE_RAW)
                m_pdf_compression = m_pdf_defaultcompression;

            if (m_pdf_defaultcompression == t2p_compress_t.T2P_COMPRESS_JPEG)
            {
                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_PALETTE) != 0)
                {
                    m_pdf_sample = (t2p_sample_t)(m_pdf_sample | t2p_sample_t.T2P_SAMPLE_REALIZE_PALETTE);
                    m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace ^ t2p_cs_t.T2P_CS_PALETTE);
                    m_tiff_pages[m_pdf_page].page_extra--;
                }
            }

            if (m_tiff_compression == COMPRESSION.COMPRESSION_JPEG)
            {
                if (m_tiff_planar == PLANARCONFIG.PLANARCONFIG_SEPARATE)
                {
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for %s with JPEG compression and separated planar configuration", input.FileName());
                    m_error = true;
                    return ;
                }
            }

            if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_REALIZE_PALETTE) != 0)
            {
                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CMYK) != 0)
                {
                    m_tiff_samplesperpixel = 4;
                    m_tiff_photometric = PHOTOMETRIC.PHOTOMETRIC_SEPARATED;
                }
                else
                {
                    m_tiff_samplesperpixel = 3;
                    m_tiff_photometric = PHOTOMETRIC.PHOTOMETRIC_RGB;
                }
            }

            result = input.GetField(TIFFTAG.TIFFTAG_TRANSFERFUNCTION);
            if (result != null)
            {
                m_tiff_transferfunction[0] = result[0].ToFloatArray();
                m_tiff_transferfunction[1] = result[1].ToFloatArray();
                m_tiff_transferfunction[2] = result[2].ToFloatArray();

                if (m_tiff_transferfunction[1] != m_tiff_transferfunction[0])
                    m_tiff_transferfunctioncount = 3;
                else
                    m_tiff_transferfunctioncount = 1;
            }
            else
            {
                m_tiff_transferfunctioncount = 0;
            }

            result = input.GetField(TIFFTAG.TIFFTAG_WHITEPOINT);
            if (result != null)
            {
                float[] xfloatp = result[0].ToFloatArray();
                m_tiff_whitechromaticities[0] = xfloatp[0];
                m_tiff_whitechromaticities[1] = xfloatp[1];
                
                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_GRAY) != 0)
                    m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace | t2p_cs_t.T2P_CS_CALGRAY);

                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_RGB) != 0)
                    m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace | t2p_cs_t.T2P_CS_CALRGB);
            }
            
            result = input.GetField(TIFFTAG.TIFFTAG_PRIMARYCHROMATICITIES);
            if (result != null)
            {
                float[] xfloatp = result[0].ToFloatArray();
                m_tiff_primarychromaticities[0] = xfloatp[0];
                m_tiff_primarychromaticities[1] = xfloatp[1];
                m_tiff_primarychromaticities[2] = xfloatp[2];
                m_tiff_primarychromaticities[3] = xfloatp[3];
                m_tiff_primarychromaticities[4] = xfloatp[4];
                m_tiff_primarychromaticities[5] = xfloatp[5];

                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_RGB) != 0)
                    m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace | t2p_cs_t.T2P_CS_CALRGB);
            }

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_LAB) != 0)
            {
                result = input.GetField(TIFFTAG.TIFFTAG_WHITEPOINT);
                if (result != null)
                {
                    float[] xfloatp = result[0].ToFloatArray();
                    m_tiff_whitechromaticities[0] = xfloatp[0];
                    m_tiff_whitechromaticities[1] = xfloatp[1];
                }
                else
                {
                    m_tiff_whitechromaticities[0] = 0.3457F; /* 0.3127F; */
                    m_tiff_whitechromaticities[1] = 0.3585F; /* 0.3290F; */
                }
            }

            result = input.GetField(TIFFTAG.TIFFTAG_ICCPROFILE);
            if (result != null)
            {
                m_tiff_iccprofilelength = result[0].ToInt();
                m_tiff_iccprofile = result[1].ToByteArray();
                m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace | t2p_cs_t.T2P_CS_ICCBASED);
            }
            else
            {
                m_tiff_iccprofilelength = 0;
                m_tiff_iccprofile = null;
            }

            if (m_tiff_bitspersample == 1 && m_tiff_samplesperpixel == 1)
                m_pdf_compression = t2p_compress_t.T2P_COMPRESS_G4;
        }

        /*
        This function returns the necessary size of a data buffer to contain the raw or 
        uncompressed image data from the input TIFF for a page.
        */
        private void read_tiff_size(Tiff input)
        {
            if (m_pdf_transcode == t2p_transcode_t.T2P_TRANSCODE_RAW)
            {
                FieldValue[] result = null;
                if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_G4)
                {
                    result = input.GetField(TIFFTAG.TIFFTAG_STRIPBYTECOUNTS);
                    int[] sbc = result[0].ToIntArray();
                    m_tiff_datasize = sbc[0];
                    return ;
                }

                if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_ZIP)
                {
                    result = input.GetField(TIFFTAG.TIFFTAG_STRIPBYTECOUNTS);
                    int[] sbc = result[0].ToIntArray();
                    m_tiff_datasize = sbc[0];
                    return ;
                }
                
                if (m_tiff_compression == COMPRESSION.COMPRESSION_JPEG)
                {
                    result = input.GetField(TIFFTAG.TIFFTAG_JPEGTABLES);
                    if (result != null)
                    {
                        int count = result[0].ToInt();
                        if (count > 4)
                        {
                            m_tiff_datasize += count;
                            m_tiff_datasize -= 2; /* don't use EOI of header */
                        }
                    }
                    else
                    {
                        m_tiff_datasize = 2; /* SOI for first strip */
                    }

                    int stripcount = input.NumberOfStrips();
                    int[] sbc = null;
                    result = input.GetField(TIFFTAG.TIFFTAG_STRIPBYTECOUNTS);
                    if (result == null)
                    {
                        Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Input file %s missing field: TIFFTAG_STRIPBYTECOUNTS", input.FileName());
                        m_error = true;
                        return;
                    }
                    else
                        sbc = result[0].ToIntArray();

                    for (uint i = 0; i < stripcount; i++)
                    {
                        m_tiff_datasize += sbc[i];
                        m_tiff_datasize -= 4; /* don't use SOI or EOI of strip */
                    }
                    
                    m_tiff_datasize += 2; /* use EOI of last strip */
                }
            }

            m_tiff_datasize = input.ScanlineSize() * m_tiff_length;
            if (m_tiff_planar == PLANARCONFIG.PLANARCONFIG_SEPARATE)
                m_tiff_datasize *= m_tiff_samplesperpixel;
        }
        
        /*
        This function returns the necessary size of a data buffer to contain the raw or 
        uncompressed image data from the input TIFF for a tile of a page.
        */
        private void read_tiff_size_tile(Tiff input, int tile)
        {
            bool edge = false;
            edge |= tile_is_right_edge(m_tiff_tiles[m_pdf_page], tile);
            edge |= tile_is_bottom_edge(m_tiff_tiles[m_pdf_page], tile);

            if (m_pdf_transcode == t2p_transcode_t.T2P_TRANSCODE_RAW)
            {
                if (edge && m_pdf_compression != t2p_compress_t.T2P_COMPRESS_JPEG)
                {
                    m_tiff_datasize = input.TileSize();
                    return;
                }
                else
                {
                    FieldValue[] result = input.GetField(TIFFTAG.TIFFTAG_TILEBYTECOUNTS);
                    int[] tbc = result[0].ToIntArray();
                    m_tiff_datasize = tbc[tile];
                    if (m_tiff_compression == COMPRESSION.COMPRESSION_JPEG)
                    {
                        result = input.GetField(TIFFTAG.TIFFTAG_JPEGTABLES);
                        if (result != null)
                        {
                            int count = result[0].ToInt();
                            if (count > 4)
                            {
                                m_tiff_datasize += count;
                                m_tiff_datasize -= 4; /* don't use EOI of header or SOI of tile */
                            }
                        }
                    }
                    return;
                }
            }

            m_tiff_datasize = input.TileSize();
            if (m_tiff_planar == PLANARCONFIG.PLANARCONFIG_SEPARATE)
                m_tiff_datasize *= m_tiff_samplesperpixel;
        }
        
        /*
        This function reads the raster image data from the input TIFF for an image and writes 
        the data to the output PDF XObject image dictionary stream.  It returns the amount written 
        or zero on error.
        */
        private int readwrite_pdf_image(Tiff input)
        {
            byte[] buffer = null;
            int bufferoffset = 0;
            int stripcount = 0;
            int max_striplength = 0;
            FieldValue[] result = null;

            if (m_pdf_transcode == t2p_transcode_t.T2P_TRANSCODE_RAW)
            {
                if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_G4)
                {
                    buffer = new byte [m_tiff_datasize];
                    input.ReadRawStrip(0, buffer, 0, m_tiff_datasize);
                    if (m_tiff_fillorder == FILLORDER.FILLORDER_LSB2MSB)
                    {
                        /*
                        * make sure is lsb-to-msb
                        * bit-endianness fill order
                        */
                        Tiff.ReverseBits(buffer, m_tiff_datasize);
                    }
                    
                    writeToFile(buffer, m_tiff_datasize);
                    return m_tiff_datasize;
                }

                if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_ZIP)
                {
                    buffer = new byte [m_tiff_datasize];
                    input.ReadRawStrip(0, buffer, 0, m_tiff_datasize);
                    if (m_tiff_fillorder == FILLORDER.FILLORDER_LSB2MSB)
                        Tiff.ReverseBits(buffer, m_tiff_datasize);

                    writeToFile(buffer, m_tiff_datasize);
                    return m_tiff_datasize;
                }
                
                if (m_tiff_compression == COMPRESSION.COMPRESSION_JPEG)
                {
                    buffer = new byte [m_tiff_datasize];
                    result = input.GetField(TIFFTAG.TIFFTAG_JPEGTABLES);
                    if (result != null)
                    {
                        int count = result[0].ToInt();
                        byte[] jpt = result[1].ToByteArray();
                        if (count > 4)
                        {
                            Array.Copy(jpt, buffer, count);
                            bufferoffset += count - 2;
                        }
                    }

                    stripcount = input.NumberOfStrips();
                    result = input.GetField(TIFFTAG.TIFFTAG_STRIPBYTECOUNTS);
                    int[] sbc = result[0].ToIntArray();
                    for (int i = 0; i < stripcount; i++)
                    {
                        if (sbc[i] > max_striplength)
                            max_striplength = sbc[i];
                    }
                    
                    byte[] stripbuffer = new byte [max_striplength];
                    for (int i = 0; i < stripcount; i++)
                    {
                        int striplength = input.ReadRawStrip(i, stripbuffer, 0, -1);
                        if (!process_jpeg_strip(stripbuffer, striplength, buffer, ref bufferoffset, i, m_tiff_length))
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Can't process JPEG data in input file %s", input.FileName());
                            m_error = true;
                            return 0;
                        }
                    }

                    buffer[bufferoffset++] = 0xff;
                    buffer[bufferoffset++] = 0xd9;
                    writeToFile(buffer, bufferoffset);
                    return bufferoffset;
                }
            }

            int stripsize = 0;
            if (m_pdf_sample == t2p_sample_t.T2P_SAMPLE_NOTHING)
            {
                buffer = new byte [m_tiff_datasize];
                stripsize = input.StripSize();
                stripcount = input.NumberOfStrips();
                for (int i = 0; i < stripcount; i++)
                {
                    int read = input.ReadEncodedStrip(i, buffer, bufferoffset, stripsize);
                    if (read == -1)
                    {
                        Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error on decoding strip %u of %s", i, input.FileName());
                        m_error = true;
                        return 0;
                    }

                    bufferoffset += read;
                }
            }
            else
            {
                byte[] samplebuffer = null;
                bool dataready = false;

                if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_PLANAR_SEPARATE_TO_CONTIG) != 0)
                {
                    int sepstripsize = input.StripSize();
                    int sepstripcount = input.NumberOfStrips();

                    stripsize = sepstripsize * m_tiff_samplesperpixel;
                    stripcount = sepstripcount / m_tiff_samplesperpixel;

                    buffer = new byte [m_tiff_datasize];
                    samplebuffer = new byte [stripsize];
                    for (int i = 0; i < stripcount; i++)
                    {
                        int samplebufferoffset = 0;
                        for (int j = 0; j < m_tiff_samplesperpixel; j++)
                        {
                            int read = input.ReadEncodedStrip(i + j * stripcount, samplebuffer, samplebufferoffset, sepstripsize);
                            if (read == -1)
                            {
                                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error on decoding strip %u of %s", i + j * stripcount, input.FileName());
                                m_error = true;
                                return 0;
                            }
                            samplebufferoffset += read;
                        }

                        sample_planar_separate_to_contig(buffer, bufferoffset, samplebuffer, samplebufferoffset);
                        bufferoffset += samplebufferoffset;
                    }

                    dataready = true;
                }

                if (!dataready)
                {
                    buffer = new byte [m_tiff_datasize];
                    stripsize = input.StripSize();
                    stripcount = input.NumberOfStrips();
                    for (int i = 0; i < stripcount; i++)
                    {
                        int read = input.ReadEncodedStrip(i, buffer, bufferoffset, stripsize);
                        if (read == -1)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error on decoding strip %u of %s", i, input.FileName());
                            m_error = true;
                            return 0;
                        }

                        bufferoffset += read;
                    }

                    if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_REALIZE_PALETTE) != 0)
                    {
                        samplebuffer = Tiff.Realloc(buffer, m_tiff_datasize, m_tiff_datasize * m_tiff_samplesperpixel);
                        buffer = samplebuffer;
                        m_tiff_datasize *= m_tiff_samplesperpixel;
                        sample_realize_palette(buffer);
                    }

                    if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_RGBA_TO_RGB) != 0)
                        m_tiff_datasize = sample_rgba_to_rgb(buffer, m_tiff_width * m_tiff_length);

                    if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_RGBAA_TO_RGB) != 0)
                        m_tiff_datasize = sample_rgbaa_to_rgb(buffer, m_tiff_width * m_tiff_length);

                    if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_YCBCR_TO_RGB) != 0)
                    {
                        samplebuffer = Tiff.Realloc(buffer, m_tiff_datasize, m_tiff_width * m_tiff_length * 4);
                        buffer = samplebuffer;

                        uint[] buffer32 = Tiff.byteArrayToUInt(buffer, 0, m_tiff_width * m_tiff_length * 4);
                        if (!input.ReadRGBAImageOriented(m_tiff_width, m_tiff_length, buffer32, ORIENTATION.ORIENTATION_TOPLEFT, false))
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Can't use ReadRGBAImageOriented to extract RGB image from %s", input.FileName());
                            m_error = true;
                            return 0;
                        }

                        Tiff.uintToByteArray(buffer32, 0, m_tiff_width * m_tiff_length, buffer, 0);

                        m_tiff_datasize = sample_abgr_to_rgb(buffer, m_tiff_width * m_tiff_length);
                    }

                    if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_LAB_SIGNED_TO_UNSIGNED) != 0)
                        m_tiff_datasize = sample_lab_signed_to_unsigned(buffer, m_tiff_width * m_tiff_length);
                }
            }

            disable(m_output);
            m_output.SetField(TIFFTAG.TIFFTAG_PHOTOMETRIC, m_tiff_photometric);
            m_output.SetField(TIFFTAG.TIFFTAG_BITSPERSAMPLE, m_tiff_bitspersample);
            m_output.SetField(TIFFTAG.TIFFTAG_SAMPLESPERPIXEL, m_tiff_samplesperpixel);
            m_output.SetField(TIFFTAG.TIFFTAG_IMAGEWIDTH, m_tiff_width);
            m_output.SetField(TIFFTAG.TIFFTAG_IMAGELENGTH, m_tiff_length);
            m_output.SetField(TIFFTAG.TIFFTAG_ROWSPERSTRIP, m_tiff_length);
            m_output.SetField(TIFFTAG.TIFFTAG_PLANARCONFIG, PLANARCONFIG.PLANARCONFIG_CONTIG);
            m_output.SetField(TIFFTAG.TIFFTAG_FILLORDER, FILLORDER.FILLORDER_MSB2LSB);

            switch (m_pdf_compression)
            {
                case t2p_compress_t.T2P_COMPRESS_NONE:
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_NONE);
                    break;
                
                case t2p_compress_t.T2P_COMPRESS_G4:
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_CCITTFAX4);
                    break;
                
                case t2p_compress_t.T2P_COMPRESS_JPEG:
                    if (m_tiff_photometric == PHOTOMETRIC.PHOTOMETRIC_YCBCR)
                    {
                        result = input.GetField(TIFFTAG.TIFFTAG_YCBCRSUBSAMPLING);
                        if (result != null)
                        {
                            ushort hor = result[0].ToUShort();
                            ushort ver = result[1].ToUShort();
                            if (hor != 0 && ver != 0)
                                m_output.SetField(TIFFTAG.TIFFTAG_YCBCRSUBSAMPLING, hor, ver);
                        }

                        result = input.GetField(TIFFTAG.TIFFTAG_REFERENCEBLACKWHITE);
                        if (result != null)
                        {
                            float[] xfloatp = result[0].ToFloatArray();
                            m_output.SetField(TIFFTAG.TIFFTAG_REFERENCEBLACKWHITE, xfloatp);
                        }
                    }

                    if (!m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_JPEG))
                    {
                        Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Unable to use JPEG compression for input %s and output %s", input.FileName(), m_output.FileName());
                        m_error = true;
                        return 0;
                    }

                    m_output.SetField(TIFFTAG.TIFFTAG_JPEGTABLESMODE, 0);

                    if ((m_pdf_colorspace & (t2p_cs_t.T2P_CS_RGB | t2p_cs_t.T2P_CS_LAB)) != 0)
                    {
                        m_output.SetField(TIFFTAG.TIFFTAG_PHOTOMETRIC, PHOTOMETRIC.PHOTOMETRIC_YCBCR);

                        if (m_tiff_photometric != PHOTOMETRIC.PHOTOMETRIC_YCBCR)
                            m_output.SetField(TIFFTAG.TIFFTAG_JPEGCOLORMODE, JPEGCOLORMODE.JPEGCOLORMODE_RGB);
                        else
                            m_output.SetField(TIFFTAG.TIFFTAG_JPEGCOLORMODE, JPEGCOLORMODE.JPEGCOLORMODE_RAW);
                    }

                    if (m_pdf_defaultcompressionquality != 0)
                        m_output.SetField(TIFFTAG.TIFFTAG_JPEGQUALITY, m_pdf_defaultcompressionquality);

                    break;
                
                case t2p_compress_t.T2P_COMPRESS_ZIP:
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_DEFLATE);
                    if (m_pdf_defaultcompressionquality % 100 != 0)
                        m_output.SetField(TIFFTAG.TIFFTAG_PREDICTOR, m_pdf_defaultcompressionquality % 100);
                    
                    if (m_pdf_defaultcompressionquality / 100 != 0)
                        m_output.SetField(TIFFTAG.TIFFTAG_ZIPQUALITY, (m_pdf_defaultcompressionquality / 100));

                    break;
                
                default:
                    break;
            }

            enable(m_output);
            m_outputwritten = 0;

            if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_JPEG && m_tiff_photometric == PHOTOMETRIC.PHOTOMETRIC_YCBCR)
                bufferoffset = m_output.WriteEncodedStrip(0, buffer, stripsize * stripcount);
            else
                bufferoffset = m_output.WriteEncodedStrip(0, buffer, m_tiff_datasize);

            buffer = null;

            if (bufferoffset == -1)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error writing encoded strip to output PDF %s", m_output.FileName());
                m_error = true;
                return 0;
            }

            return m_outputwritten;
        }

        /*
        * This function reads the raster image data from the input TIFF for an image
        * tile and writes the data to the output PDF XObject image dictionary stream
        * for the tile.  It returns the amount written or zero on error.
        */
        private int readwrite_pdf_image_tile(Tiff input, int tile)
        {
            bool edge = false;
            edge |= tile_is_right_edge(m_tiff_tiles[m_pdf_page], tile);
            edge |= tile_is_bottom_edge(m_tiff_tiles[m_pdf_page], tile);

            byte[] buffer = null;
            int bufferoffset = 0;
            FieldValue[] result = null;

            if ((m_pdf_transcode == t2p_transcode_t.T2P_TRANSCODE_RAW) && (!edge || (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_JPEG)))
            {
                if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_G4)
                {
                    buffer = new byte [m_tiff_datasize];
                    input.ReadRawTile(tile, buffer, 0, m_tiff_datasize);
                    if (m_tiff_fillorder == FILLORDER.FILLORDER_LSB2MSB)
                        Tiff.ReverseBits(buffer, m_tiff_datasize);

                    writeToFile(buffer, m_tiff_datasize);
                    return m_tiff_datasize;
                }
                
                if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_ZIP)
                {
                    buffer = new byte [m_tiff_datasize];
                    input.ReadRawTile(tile, buffer, 0, m_tiff_datasize);
                    if (m_tiff_fillorder == FILLORDER.FILLORDER_LSB2MSB)
                        Tiff.ReverseBits(buffer, m_tiff_datasize);

                    writeToFile(buffer, m_tiff_datasize);
                    return m_tiff_datasize;
                }
                
                if (m_tiff_compression == COMPRESSION.COMPRESSION_JPEG)
                {
                    byte[] table_end = new byte[2];
                    buffer = new byte [m_tiff_datasize];
                    result = input.GetField(TIFFTAG.TIFFTAG_JPEGTABLES);
                    if (result != null)
                    {
                        int count = result[0].ToInt();
                        byte[] jpt = result[1].ToByteArray();
                        if (count > 0)
                        {
                            Array.Copy(jpt, buffer, count);
                            bufferoffset += count - 2;
                            table_end[0] = buffer[bufferoffset - 2];
                            table_end[1] = buffer[bufferoffset - 1];
                        }

                        if (count > 0)
                        {
                            int xuint32 = bufferoffset;
                            bufferoffset += input.ReadRawTile(tile, buffer, bufferoffset - 2, -1);
                            buffer[xuint32 - 2] = table_end[0];
                            buffer[xuint32 - 1] = table_end[1];
                        }
                        else
                        {
                            bufferoffset += input.ReadRawTile(tile, buffer, bufferoffset, -1);
                        }
                    }

                    writeToFile(buffer, bufferoffset);
                    return bufferoffset;
                }
            }

            if (m_pdf_sample == t2p_sample_t.T2P_SAMPLE_NOTHING)
            {
                buffer = new byte [m_tiff_datasize];
                int read = input.ReadEncodedTile(tile, buffer, bufferoffset, m_tiff_datasize);
                if (read == -1)
                {
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error on decoding tile %u of %s", tile, input.FileName());
                    m_error = true;
                    return 0;
                }
            }
            else
            {
                if (m_pdf_sample == t2p_sample_t.T2P_SAMPLE_PLANAR_SEPARATE_TO_CONTIG)
                {
                    int septilesize = input.TileSize();
                    int septilecount = input.NumberOfTiles();
                    int tilecount = septilecount / m_tiff_samplesperpixel;
                    buffer = new byte [m_tiff_datasize];
                    byte[] samplebuffer = new byte [m_tiff_datasize];
                    int samplebufferoffset = 0;
                    for (ushort i = 0; i < m_tiff_samplesperpixel; i++)
                    {
                        int read = input.ReadEncodedTile(tile + i * tilecount, samplebuffer, samplebufferoffset, septilesize);
                        if (read == -1)
                        {
                            Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error on decoding tile %u of %s", tile + i * tilecount, input.FileName());
                            m_error = true;
                            return 0;
                        }

                        samplebufferoffset += read;
                    }

                    sample_planar_separate_to_contig(buffer, bufferoffset, samplebuffer, samplebufferoffset);
                    bufferoffset += samplebufferoffset;
                }

                if (buffer == null)
                {
                    buffer = new byte [m_tiff_datasize];
                    int read = input.ReadEncodedTile(tile, buffer, bufferoffset, m_tiff_datasize);
                    if (read == -1)
                    {
                        Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error on decoding tile %u of %s", tile, input.FileName());
                        m_error = true;
                        return 0;
                    }
                }

                if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_RGBA_TO_RGB) != 0)
                    m_tiff_datasize = sample_rgba_to_rgb(buffer, m_tiff_tiles[m_pdf_page].tiles_tilewidth * m_tiff_tiles[m_pdf_page].tiles_tilelength);

                if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_RGBAA_TO_RGB) != 0)
                    m_tiff_datasize = sample_rgbaa_to_rgb(buffer, m_tiff_tiles[m_pdf_page].tiles_tilewidth * m_tiff_tiles[m_pdf_page].tiles_tilelength);

                if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_YCBCR_TO_RGB) != 0)
                {
                    Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "No support for YCbCr to RGB in tile for %s", input.FileName());
                    m_error = true;
                    return 0;
                }

                if ((m_pdf_sample & t2p_sample_t.T2P_SAMPLE_LAB_SIGNED_TO_UNSIGNED) != 0)
                    m_tiff_datasize = sample_lab_signed_to_unsigned(buffer, m_tiff_tiles[m_pdf_page].tiles_tilewidth * m_tiff_tiles[m_pdf_page].tiles_tilelength);
            }

            if (tile_is_right_edge(m_tiff_tiles[m_pdf_page], tile))
                tile_collapse_left(buffer, input.TileRowSize(), m_tiff_tiles[m_pdf_page].tiles_tilewidth, m_tiff_tiles[m_pdf_page].tiles_edgetilewidth, m_tiff_tiles[m_pdf_page].tiles_tilelength);

            disable(m_output);
            m_output.SetField(TIFFTAG.TIFFTAG_PHOTOMETRIC, m_tiff_photometric);
            m_output.SetField(TIFFTAG.TIFFTAG_BITSPERSAMPLE, m_tiff_bitspersample);
            m_output.SetField(TIFFTAG.TIFFTAG_SAMPLESPERPIXEL, m_tiff_samplesperpixel);

            if (!tile_is_right_edge(m_tiff_tiles[m_pdf_page], tile))
                m_output.SetField(TIFFTAG.TIFFTAG_IMAGEWIDTH, m_tiff_tiles[m_pdf_page].tiles_tilewidth);
            else
                m_output.SetField(TIFFTAG.TIFFTAG_IMAGEWIDTH, m_tiff_tiles[m_pdf_page].tiles_edgetilewidth);

            if (!tile_is_bottom_edge(m_tiff_tiles[m_pdf_page], tile))
            {
                m_output.SetField(TIFFTAG.TIFFTAG_IMAGELENGTH, m_tiff_tiles[m_pdf_page].tiles_tilelength);
                m_output.SetField(TIFFTAG.TIFFTAG_ROWSPERSTRIP, m_tiff_tiles[m_pdf_page].tiles_tilelength);
            }
            else
            {
                m_output.SetField(TIFFTAG.TIFFTAG_IMAGELENGTH, m_tiff_tiles[m_pdf_page].tiles_edgetilelength);
                m_output.SetField(TIFFTAG.TIFFTAG_ROWSPERSTRIP, m_tiff_tiles[m_pdf_page].tiles_edgetilelength);
            }

            m_output.SetField(TIFFTAG.TIFFTAG_PLANARCONFIG, PLANARCONFIG.PLANARCONFIG_CONTIG);
            m_output.SetField(TIFFTAG.TIFFTAG_FILLORDER, FILLORDER.FILLORDER_MSB2LSB);

            switch (m_pdf_compression)
            {
                case t2p_compress_t.T2P_COMPRESS_NONE:
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_NONE);
                    break;

                case t2p_compress_t.T2P_COMPRESS_G4:
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_CCITTFAX4);
                    break;
                
                case t2p_compress_t.T2P_COMPRESS_JPEG:
                    if (m_tiff_photometric == PHOTOMETRIC.PHOTOMETRIC_YCBCR)
                    {
                        result = input.GetField(TIFFTAG.TIFFTAG_YCBCRSUBSAMPLING);
                        if (result != null)
                        {
                            ushort hor = result[0].ToUShort();
                            ushort ver = result[1].ToUShort();
                            if (hor != 0 && ver != 0)
                                m_output.SetField(TIFFTAG.TIFFTAG_YCBCRSUBSAMPLING, hor, ver);
                        }

                        result = input.GetField(TIFFTAG.TIFFTAG_REFERENCEBLACKWHITE);
                        if (result != null)
                        {
                            float[] xfloatp = result[0].ToFloatArray();
                            m_output.SetField(TIFFTAG.TIFFTAG_REFERENCEBLACKWHITE, xfloatp);
                        }
                    }
                    
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_JPEG);
                    m_output.SetField(TIFFTAG.TIFFTAG_JPEGTABLESMODE, 0); /* JPEGTABLESMODE_NONE */

                    if ((m_pdf_colorspace & (t2p_cs_t.T2P_CS_RGB | t2p_cs_t.T2P_CS_LAB)) != 0)
                    {
                        m_output.SetField(TIFFTAG.TIFFTAG_PHOTOMETRIC, PHOTOMETRIC.PHOTOMETRIC_YCBCR);
                        if (m_tiff_photometric != PHOTOMETRIC.PHOTOMETRIC_YCBCR)
                            m_output.SetField(TIFFTAG.TIFFTAG_JPEGCOLORMODE, JPEGCOLORMODE.JPEGCOLORMODE_RGB);
                        else
                            m_output.SetField(TIFFTAG.TIFFTAG_JPEGCOLORMODE, JPEGCOLORMODE.JPEGCOLORMODE_RAW);
                    }

                    if (m_pdf_defaultcompressionquality != 0)
                        m_output.SetField(TIFFTAG.TIFFTAG_JPEGQUALITY, m_pdf_defaultcompressionquality);

                    break;

                case t2p_compress_t.T2P_COMPRESS_ZIP:
                    m_output.SetField(TIFFTAG.TIFFTAG_COMPRESSION, COMPRESSION.COMPRESSION_DEFLATE);
                    if (m_pdf_defaultcompressionquality % 100 != 0)
                        m_output.SetField(TIFFTAG.TIFFTAG_PREDICTOR, m_pdf_defaultcompressionquality % 100);

                    if (m_pdf_defaultcompressionquality / 100 != 0)
                        m_output.SetField(TIFFTAG.TIFFTAG_ZIPQUALITY, (m_pdf_defaultcompressionquality / 100));

                    break;

                default:
                    break;
            }

            enable(m_output);
            m_outputwritten = 0;
            bufferoffset = m_output.WriteEncodedStrip(0, buffer, m_output.StripSize());
            buffer = null;

            if (bufferoffset == -1)
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error writing encoded tile to output PDF %s", m_output.FileName());
                m_error = true;
                return 0;
            }

            return m_outputwritten;
        }
        
        /*
        * This function calls WriteDirectory on the output after blanking its
        * output by replacing the read, write, and seek procedures with empty
        * implementations, then it replaces the original implementations.
        */
        private void write_advance_directory()
        {
            disable(m_output);
            if (!m_output.WriteDirectory())
            {
                Tiff.Error(Tiff2PdfConstants.TIFF2PDF_MODULE, "Error writing virtual directory to output PDF %s", m_output.FileName());
                m_error = true;
                return ;
            }

            enable(m_output);
        }

        private void sample_planar_separate_to_contig(byte[] buffer, int bufferOffset, byte[] samplebuffer, int samplebuffersize)
        {
            int stride = samplebuffersize / m_tiff_samplesperpixel;
            for (int i = 0; i < stride; i++)
            {
                for (int j = 0; j < m_tiff_samplesperpixel; j++)
                {
                    buffer[bufferOffset + i * m_tiff_samplesperpixel + j] = samplebuffer[i + j * stride];
                }
            }
        }

        private void sample_realize_palette(byte[] buffer)
        {
            int sample_count = m_tiff_width * m_tiff_length;
            ushort component_count = m_tiff_samplesperpixel;

            for (int i = sample_count; i > 0; i--)
            {
                int palette_offset = buffer[i - 1] * component_count;
                int sample_offset = (i - 1) * component_count;
                for (uint j = 0; j < component_count; j++)
                {
                    buffer[sample_offset + j] = m_pdf_palette[palette_offset + j];
                }
            }
        }

        /* 
        This function writes the PDF header to output.
        */
        private int write_pdf_header()
        {
            string buffer = string.Format("%%PDF-{0}.{1} ", m_pdf_majorversion & 0xff, m_pdf_minorversion & 0xff);
            
            int written = writeToFile(buffer);
            written += writeToFile("\n%");

            byte[] octals = new byte [4];
            octals[0] = Convert.ToByte("342", 8);
            octals[1] = Convert.ToByte("343", 8);
            octals[2] = Convert.ToByte("317", 8);
            octals[3] = Convert.ToByte("323", 8);
            written += writeToFile(octals, octals.Length);

            written += writeToFile("\n");

            return written;
        }

        /*
        This function writes the PDF Catalog structure to output.
        */
        private int write_pdf_catalog()
        {
            int written = writeToFile("<< \n/Type /Catalog \n/Pages ");
    
            string buffer = string.Format("{0}", m_pdf_pages);
            written += writeToFile(buffer);
            written += writeToFile(" 0 R \n");

            if (m_pdf_fitwindow)
                written += writeToFile("/ViewerPreferences <</FitWindow true>>\n");

            written += writeToFile(">>\n");
            return written;
        }

        /*
        This function writes the PDF Info structure to output.
        */
        private int write_pdf_info(Tiff input)
        {
            if (m_pdf_datetime == null)
                pdf_tifftime(input);

            int written = 0;
            if (strlen(m_pdf_datetime) > 0)
            {
                written += writeToFile("<< \n/CreationDate ");
                written += write_pdf_string(m_pdf_datetime);
                written += writeToFile("\n/ModDate ");
                written += write_pdf_string(m_pdf_datetime);
            }

            /*written += t2pWriteFile("\n/Producer ");

            //char buffer[512];
            memset(buffer, 0x00, sizeof(buffer));
            sprintf(buffer, "libtiff / tiff2pdf - %d", TIFFLIB_VERSION);
            written += write_pdf_string(buffer);*/
            written += writeToFile("\n");

            FieldValue[] result = null;
            if (m_pdf_creator != null)
            {
                if (strlen(m_pdf_creator) > 0)
                {
                    byte[] buf = m_pdf_creator;
                    if (strlen(m_pdf_creator) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(m_pdf_creator, buf, 511);
                    }

                    written += writeToFile("/Creator ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }
            else
            {
                result = input.GetField(TIFFTAG.TIFFTAG_SOFTWARE);
                if (result != null)
                {
                    byte[] info = result[0].ToByteArray();
                    byte[] buf = info;
                    if (strlen(info) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(info, buf, 511);
                    }

                    written += writeToFile("/Creator ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }

            if (m_pdf_author != null)
            {
                if (strlen(m_pdf_author) > 0)
                {
                    byte[] buf = m_pdf_author;
                    if (strlen(m_pdf_author) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(m_pdf_author, buf, 511);
                    }

                    written += writeToFile("/Author ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }
            else
            {
                result = input.GetField(TIFFTAG.TIFFTAG_ARTIST);
                if (result != null)
                {
                    byte[] info = result[0].ToByteArray();
                    byte[] buf = info;
                    if (strlen(info) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(info, buf, 511);
                    }

                    written += writeToFile("/Author ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
                else
                {
                    result = input.GetField(TIFFTAG.TIFFTAG_COPYRIGHT);
                    if (result != null)
                    {
                        byte[] info = result[0].ToByteArray();
                        byte[] buf = info;
                        if (strlen(info) > 511)
                        {
                            buf = new byte[512];
                            Array.Copy(info, buf, 511);
                        }

                        written += writeToFile("/Author ");
                        written += write_pdf_string(buf);
                        written += writeToFile("\n");
                    }
                }
            }

            if (m_pdf_title != null)
            {
                if (strlen(m_pdf_title) > 0)
                {
                    byte[] buf = m_pdf_title;
                    if (strlen(m_pdf_title) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(m_pdf_title, buf, 511);
                    }

                    written += writeToFile("/Title ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }
            else
            {
                result = input.GetField(TIFFTAG.TIFFTAG_DOCUMENTNAME);
                if (result != null)
                {
                    byte[] info = result[0].ToByteArray();
                    byte[] buf = info;
                    if (strlen(info) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(info, buf, 511);
                    }

                    written += writeToFile("/Title ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }
            
            if (m_pdf_subject != null)
            {
                if (strlen(m_pdf_subject) > 0)
                {
                    byte[] buf = m_pdf_subject;
                    if (strlen(m_pdf_subject) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(m_pdf_subject, buf, 511);
                    }

                    written += writeToFile("/Subject ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }
            else
            {
                result = input.GetField(TIFFTAG.TIFFTAG_IMAGEDESCRIPTION);
                if (result != null)
                {
                    byte[] info = result[0].ToByteArray();
                    byte[] buf = info;
                    if (strlen(info) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(info, buf, 511);
                    }

                    written += writeToFile("/Subject ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }

            if (m_pdf_keywords != null)
            {
                if (strlen(m_pdf_keywords) > 0)
                {
                    byte[] buf = m_pdf_keywords;
                    if (strlen(m_pdf_keywords) > 511)
                    {
                        buf = new byte[512];
                        Array.Copy(m_pdf_keywords, buf, 511);
                    }

                    written += writeToFile("/Keywords ");
                    written += write_pdf_string(buf);
                    written += writeToFile("\n");
                }
            }

            written += writeToFile(">> \n");
            return written;
        }

        private int strlen(byte[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] == 0)
                    return (i + 1);
            }

            return buffer.Length;
        }

        /*
        * This function fills a string of a T2P struct with the current time as a PDF
        * date string, it is called by pdf_tifftime.
        */
        private void pdf_currenttime()
        {
            int timenow = 1247603070; // 15-07-2009 XXXX
            DateTime dt = new DateTime(1970, 1, 1).AddSeconds(timenow).ToLocalTime();

            //timenow=time(0);

            string s = string.Format("D:{0:0000}{1:00}{2:00}{3:00}{4:00}{5:00}", 
                dt.Year % 65536, dt.Month % 256, dt.Day % 256, dt.Hour % 256, 
                dt.Minute % 256, dt.Second % 256);

            m_pdf_datetime = Encoding.ASCII.GetBytes(s);
        }
        
        /*
        * This function fills a string of a T2P struct with the date and time of a
        * TIFF file if it exists or the current time as a PDF date string.
        */
        private void pdf_tifftime(Tiff input)
        {
            m_pdf_datetime = new byte [19];
            m_pdf_datetime[16] = 0;

            FieldValue[] result = input.GetField(TIFFTAG.TIFFTAG_DATETIME);
            if (result != null && (result[0].ToString()).Length >= 19)
            {
                string datetime = result[0].ToString();
                m_pdf_datetime[0] = (byte)'D';
                m_pdf_datetime[1] = (byte)':';
                m_pdf_datetime[2] = (byte)datetime[0];
                m_pdf_datetime[3] = (byte)datetime[1];
                m_pdf_datetime[4] = (byte)datetime[2];
                m_pdf_datetime[5] = (byte)datetime[3];
                m_pdf_datetime[6] = (byte)datetime[5];
                m_pdf_datetime[7] = (byte)datetime[6];
                m_pdf_datetime[8] = (byte)datetime[8];
                m_pdf_datetime[9] = (byte)datetime[9];
                m_pdf_datetime[10] = (byte)datetime[11];
                m_pdf_datetime[11] = (byte)datetime[12];
                m_pdf_datetime[12] = (byte)datetime[14];
                m_pdf_datetime[13] = (byte)datetime[15];
                m_pdf_datetime[14] = (byte)datetime[17];
                m_pdf_datetime[15] = (byte)datetime[18];
            }
            else
            {
                pdf_currenttime();
            }
        }

        /*
        * This function writes a PDF Pages Tree structure to output.
        */
        private int write_pdf_pages()
        {
            int written = writeToFile("<< \n/Type /Pages \n/Kids [ ");
            int page = m_pdf_pages + 1;

            string buffer = null;
            for (ushort i = 0; i < m_tiff_pagecount; i++)
            {
                buffer = string.Format("{0}", page);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");

                if (((i + 1) % 8) == 0)
                    written += writeToFile("\n");

                page += 3;
                page += m_tiff_pages[i].page_extra;

                if (m_tiff_pages[i].page_tilecount > 0)
                    page += (2 * m_tiff_pages[i].page_tilecount);
                else
                    page += 2;
            }

            written += writeToFile("] \n/Count ");
            buffer = string.Format("{0}", m_tiff_pagecount);
            written += writeToFile(buffer);
            written += writeToFile(" \n>> \n");

            return written;
        }

        /*
        This function composes the page size and image and tile locations on a page.
        */
        private void compose_pdf_page()
        {
            m_pdf_xres = m_tiff_xres;
            m_pdf_yres = m_tiff_yres;

            if (m_pdf_overrideres)
            {
                m_pdf_xres = m_pdf_defaultxres;
                m_pdf_yres = m_pdf_defaultyres;
            }
            
            if (m_pdf_xres == 0.0)
                m_pdf_xres = m_pdf_defaultxres;
            
            if (m_pdf_yres == 0.0)
                m_pdf_yres = m_pdf_defaultyres;

            if ((m_tiff_resunit != RESUNIT.RESUNIT_CENTIMETER && m_tiff_resunit != RESUNIT.RESUNIT_INCH) && (m_tiff_xres < Tiff2PdfConstants.PS_UNIT_SIZE && m_tiff_yres < Tiff2PdfConstants.PS_UNIT_SIZE))
            {
                // apply special processing for case when resolution 
                // unit is unspecified and resolution is "very low" (less then Tiff2PdfConstants.PS_UNIT_SIZE)
                m_pdf_imagewidth = ((float)m_tiff_width) / m_pdf_xres;
                m_pdf_imagelength = ((float)m_tiff_length) / m_pdf_yres;
            }
            else
            {
                m_pdf_imagewidth = ((float)m_tiff_width) * Tiff2PdfConstants.PS_UNIT_SIZE / m_pdf_xres;
                m_pdf_imagelength = ((float)m_tiff_length) * Tiff2PdfConstants.PS_UNIT_SIZE / m_pdf_yres;
            }

            if (m_pdf_overridepagesize)
            {
                m_pdf_pagewidth = m_pdf_defaultpagewidth;
                m_pdf_pagelength = m_pdf_defaultpagelength;
            }
            else
            {
                m_pdf_pagewidth = m_pdf_imagewidth;
                m_pdf_pagelength = m_pdf_imagelength;
            }

            m_pdf_mediabox.x1 = 0.0f;
            m_pdf_mediabox.y1 = 0.0f;
            m_pdf_mediabox.x2 = m_pdf_pagewidth;
            m_pdf_mediabox.y2 = m_pdf_pagelength;
            m_pdf_imagebox.x1 = 0.0f;
            m_pdf_imagebox.y1 = 0.0f;
            m_pdf_imagebox.x2 = m_pdf_imagewidth;
            m_pdf_imagebox.y2 = m_pdf_imagelength;

            if (m_pdf_overridepagesize)
            {
                m_pdf_imagebox.x1 += (m_pdf_pagewidth - m_pdf_imagewidth) / 2.0F;
                m_pdf_imagebox.y1 += (m_pdf_pagelength - m_pdf_imagelength) / 2.0F;
                m_pdf_imagebox.x2 += (m_pdf_pagewidth - m_pdf_imagewidth) / 2.0F;
                m_pdf_imagebox.y2 += (m_pdf_pagelength - m_pdf_imagelength) / 2.0F;
            }

            if (m_tiff_orientation > ORIENTATION.ORIENTATION_BOTLEFT)
            {
                float f = m_pdf_mediabox.x2;
                m_pdf_mediabox.x2 = m_pdf_mediabox.y2;
                m_pdf_mediabox.y2 = f;
            }

            T2P_TILE[] tiles = null;
            int istiled = ((m_tiff_tiles[m_pdf_page]).tiles_tilecount == 0) ? 0 : 1;
            if (istiled == 0)
            {
                compose_pdf_page_orient(m_pdf_imagebox, m_tiff_orientation);
                return ;
            }
            else
            {
                int tilewidth = m_tiff_tiles[m_pdf_page].tiles_tilewidth;
                int tilelength = m_tiff_tiles[m_pdf_page].tiles_tilelength;
                int tilecountx = (m_tiff_width + tilewidth - 1) / tilewidth;
                m_tiff_tiles[m_pdf_page].tiles_tilecountx = tilecountx;
                int tilecounty = (m_tiff_length + tilelength - 1) / tilelength;
                m_tiff_tiles[m_pdf_page].tiles_tilecounty = tilecounty;
                m_tiff_tiles[m_pdf_page].tiles_edgetilewidth = m_tiff_width % tilewidth;
                m_tiff_tiles[m_pdf_page].tiles_edgetilelength = m_tiff_length % tilelength;
                tiles = m_tiff_tiles[m_pdf_page].tiles_tiles;
                
                uint i = 0;
                uint i2 = 0;
                T2P_BOX boxp = null;
                for (i2 = 0; i2 < tilecounty - 1; i2++)
                {
                    for (i = 0; i < tilecountx - 1; i++)
                    {
                        boxp = tiles[i2 * tilecountx + i].tile_box;
                        boxp.x1 = m_pdf_imagebox.x1 + ((float)(m_pdf_imagewidth * i * tilewidth) / (float)m_tiff_width);
                        boxp.x2 = m_pdf_imagebox.x1 + ((float)(m_pdf_imagewidth * (i + 1) * tilewidth) / (float)m_tiff_width);
                        boxp.y1 = m_pdf_imagebox.y2 - ((float)(m_pdf_imagelength * (i2 + 1) * tilelength) / (float)m_tiff_length);
                        boxp.y2 = m_pdf_imagebox.y2 - ((float)(m_pdf_imagelength * i2 * tilelength) / (float)m_tiff_length);
                    }

                    boxp = tiles[i2 * tilecountx + i].tile_box;
                    boxp.x1 = m_pdf_imagebox.x1 + ((float)(m_pdf_imagewidth * i * tilewidth) / (float)m_tiff_width);
                    boxp.x2 = m_pdf_imagebox.x2;
                    boxp.y1 = m_pdf_imagebox.y2 - ((float)(m_pdf_imagelength * (i2 + 1) * tilelength) / (float)m_tiff_length);
                    boxp.y2 = m_pdf_imagebox.y2 - ((float)(m_pdf_imagelength * i2 * tilelength) / (float)m_tiff_length);
                }

                for (i = 0; i < tilecountx - 1; i++)
                {
                    boxp = tiles[i2 * tilecountx + i].tile_box;
                    boxp.x1 = m_pdf_imagebox.x1 + ((float)(m_pdf_imagewidth * i * tilewidth) / (float)m_tiff_width);
                    boxp.x2 = m_pdf_imagebox.x1 + ((float)(m_pdf_imagewidth *(i + 1) * tilewidth) / (float)m_tiff_width);
                    boxp.y1 = m_pdf_imagebox.y1;
                    boxp.y2 = m_pdf_imagebox.y2 - ((float)(m_pdf_imagelength * i2 * tilelength) / (float)m_tiff_length);
                }

                boxp = tiles[i2 * tilecountx + i].tile_box;
                boxp.x1 = m_pdf_imagebox.x1 + ((float)(m_pdf_imagewidth * i * tilewidth) / (float)m_tiff_width);
                boxp.x2 = m_pdf_imagebox.x2;
                boxp.y1 = m_pdf_imagebox.y1;
                boxp.y2 = m_pdf_imagebox.y2 - ((float)(m_pdf_imagelength * i2 * tilelength) / (float)m_tiff_length);
            }

            if (m_tiff_orientation == 0 || m_tiff_orientation == ORIENTATION.ORIENTATION_TOPLEFT)
            {
                for (uint i = 0; i < m_tiff_tiles[m_pdf_page].tiles_tilecount; i++)
                    compose_pdf_page_orient(tiles[i].tile_box, 0);

                return ;
            }

            for (uint i = 0; i < m_tiff_tiles[m_pdf_page].tiles_tilecount; i++)
            {
                T2P_BOX boxp = tiles[i].tile_box;
                boxp.x1 -= m_pdf_imagebox.x1;
                boxp.x2 -= m_pdf_imagebox.x1;
                boxp.y1 -= m_pdf_imagebox.y1;
                boxp.y2 -= m_pdf_imagebox.y1;

                if (m_tiff_orientation == ORIENTATION.ORIENTATION_TOPRIGHT || m_tiff_orientation == ORIENTATION.ORIENTATION_BOTRIGHT)
                {
                    boxp.x1 = m_pdf_imagebox.x2 - m_pdf_imagebox.x1 - boxp.x1;
                    boxp.x2 = m_pdf_imagebox.x2 - m_pdf_imagebox.x1 - boxp.x2;
                }
                
                if (m_tiff_orientation == ORIENTATION.ORIENTATION_BOTRIGHT || m_tiff_orientation == ORIENTATION.ORIENTATION_BOTLEFT)
                {
                    boxp.y1 = m_pdf_imagebox.y2 - m_pdf_imagebox.y1 - boxp.y1;
                    boxp.y2 = m_pdf_imagebox.y2 - m_pdf_imagebox.y1 - boxp.y2;
                }
                
                if (m_tiff_orientation == ORIENTATION.ORIENTATION_LEFTBOT || m_tiff_orientation == ORIENTATION.ORIENTATION_LEFTTOP)
                {
                    boxp.y1 = m_pdf_imagebox.y2 - m_pdf_imagebox.y1 - boxp.y1;
                    boxp.y2 = m_pdf_imagebox.y2 - m_pdf_imagebox.y1 - boxp.y2;
                }
                
                if (m_tiff_orientation == ORIENTATION.ORIENTATION_LEFTTOP || m_tiff_orientation == ORIENTATION.ORIENTATION_RIGHTTOP)
                {
                    boxp.x1 = m_pdf_imagebox.x2 - m_pdf_imagebox.x1 - boxp.x1;
                    boxp.x2 = m_pdf_imagebox.x2 - m_pdf_imagebox.x1 - boxp.x2;
                }
                
                if (m_tiff_orientation > ORIENTATION.ORIENTATION_BOTLEFT)
                {
                    float f = boxp.x1;
                    boxp.x1 = boxp.y1;
                    boxp.y1 = f;
                    f = boxp.x2;
                    boxp.x2 = boxp.y2;
                    boxp.y2 = f;
                    compose_pdf_page_orient_flip(boxp, m_tiff_orientation);
                }
                else
                {
                    compose_pdf_page_orient(boxp, m_tiff_orientation);
                }
            }
        }
        
        /*
        * 	This function writes a PDF Image XObject Colorspace name to output.
        */
        private int write_pdf_xobject_cs()
        {
            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_ICCBASED) != 0)
                return write_pdf_xobject_icccs();

            int written = 0;
            string buffer = null;

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_PALETTE) != 0)
            {
                written += writeToFile("[ /Indexed ");
                m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace ^ t2p_cs_t.T2P_CS_PALETTE);
                written += write_pdf_xobject_cs();
                m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace | t2p_cs_t.T2P_CS_PALETTE);
                buffer = string.Format("{0}", (0x0001 << m_tiff_bitspersample) - 1);
                written += writeToFile(buffer);
                written += writeToFile(" ");
                buffer = string.Format("{0}", m_pdf_palettecs);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ]\n");
                return written;
            }

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_BILEVEL) != 0)
                written += writeToFile("/DeviceGray \n");

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_GRAY) != 0)
            {
                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CALGRAY) != 0)
                    written += write_pdf_xobject_calcs();
                else
                    written += writeToFile("/DeviceGray \n");
            }

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_RGB) != 0)
            {
                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CALRGB) != 0)
                    written += write_pdf_xobject_calcs();
                else
                    written += writeToFile("/DeviceRGB \n");
            }

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CMYK) != 0)
                written += writeToFile("/DeviceCMYK \n");

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_LAB) != 0)
            {
                written += writeToFile("[/Lab << \n");
                written += writeToFile("/WhitePoint ");
                
                float X_W = m_tiff_whitechromaticities[0];
                float Y_W = m_tiff_whitechromaticities[1];
                float Z_W = 1.0F - (X_W + Y_W);
                X_W /= Y_W;
                Z_W /= Y_W;
                Y_W = 1.0F;
                buffer = string.Format("[%.4f %.4f %.4f] \n", X_W, Y_W, Z_W);
                written += writeToFile(buffer);
                
                X_W = 0.3457F; /* 0.3127F; */ /* D50, commented D65 */
                Y_W = 0.3585F; /* 0.3290F; */
                Z_W = 1.0F - (X_W + Y_W);
                X_W /= Y_W;
                Z_W /= Y_W;
                Y_W = 1.0F;
                buffer = string.Format("[%.4f %.4f %.4f] \n", X_W, Y_W, Z_W);
                written += writeToFile(buffer);
                written += writeToFile("/Range ");
                buffer = string.Format("[{0} {1} {2} {3}] \n", m_pdf_labrange[0], m_pdf_labrange[1], m_pdf_labrange[2], m_pdf_labrange[3]);
                written += writeToFile(buffer);
                written += writeToFile(">>] \n");
            }

            return written;
        }
        
        private int write_pdf_transfer()
        {
            string buffer = null;
            int written = writeToFile("<< /Type /ExtGState \n/TR ");

            if (m_tiff_transferfunctioncount == 1)
            {
                buffer = string.Format("{0}", m_pdf_xrefcount + 1);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");
            }
            else
            {
                written += writeToFile("[ ");
                buffer = string.Format("{0}", m_pdf_xrefcount + 1);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");
                buffer = string.Format("{0}", m_pdf_xrefcount + 2);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");
                buffer = string.Format("{0}", m_pdf_xrefcount + 3);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");
                written += writeToFile("/Identity ] ");
            }

            written += writeToFile(" >> \n");
            return written;
        }

        private int write_pdf_transfer_dict()
        {
            int written = writeToFile("/FunctionType 0 \n");
            written += writeToFile("/Domain [0.0 1.0] \n");
            written += writeToFile("/Range [0.0 1.0] \n");

            string buffer = string.Format("/Size [{0}] \n", (1 << m_tiff_bitspersample));
            written += writeToFile(buffer);
            written += writeToFile("/BitsPerSample 16 \n");
            written += write_pdf_stream_dict(((int)1) << (m_tiff_bitspersample + 1), 0);

            return written;
        }

        private int write_pdf_transfer_stream(ushort i)
        {
            int floatLength = m_tiff_transferfunction[i].Length;
            byte[] bytes = new byte[floatLength * sizeof(float)];
            int bytesPos = 0;
            for (int pos = 0; pos < floatLength; pos++)
            {
                byte[] temp = BitConverter.GetBytes(m_tiff_transferfunction[i][pos]);
                Array.Copy(temp, 0, bytes, bytesPos, temp.Length);
                bytesPos += temp.Length;
            }

            return write_pdf_stream(bytes, (1 << (m_tiff_bitspersample + 1)));
        }
        
        /*
        This function writes a PDF Image XObject Colorspace array to output.
        */
        private int write_pdf_xobject_calcs()
        {
            int written = writeToFile("[");

            float X_W = 0.0f;
            float Y_W = 0.0f;
            float Z_W = 0.0f;    
            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CALGRAY) != 0)
            {
                written += writeToFile("/CalGray ");
                X_W = m_tiff_whitechromaticities[0];
                Y_W = m_tiff_whitechromaticities[1];
                Z_W = 1.0F - (X_W + Y_W);
                X_W /= Y_W;
                Z_W /= Y_W;
                Y_W = 1.0F;
            }

            float X_R = 0.0f;
            float Y_R = 0.0f;
            float Z_R = 0.0f;
            float X_G = 0.0f;
            float Y_G = 0.0f;
            float Z_G = 0.0f;
            float X_B = 0.0f;
            float Y_B = 0.0f;
            float Z_B = 0.0f;
            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CALRGB) != 0)
            {
                written += writeToFile("/CalRGB ");
                float x_w = m_tiff_whitechromaticities[0];
                float y_w = m_tiff_whitechromaticities[1];
                float x_r = m_tiff_primarychromaticities[0];
                float y_r = m_tiff_primarychromaticities[1];
                float x_g = m_tiff_primarychromaticities[2];
                float y_g = m_tiff_primarychromaticities[3];
                float x_b = m_tiff_primarychromaticities[4];
                float y_b = m_tiff_primarychromaticities[5];

                const float R = 1.0f;
                const float G = 1.0f;
                const float B = 1.0f;

                float z_w = y_w * ((x_g - x_b) * y_r - (x_r - x_b) * y_g + (x_r - x_g) * y_b);
                Y_R = (y_r / R) * ((x_g - x_b) * y_w - (x_w - x_b) * y_g + (x_w - x_g) * y_b) / z_w;
                X_R = Y_R * x_r / y_r;
                Z_R = Y_R * (((1 - x_r) / y_r) - 1);
                Y_G = ((0.0F - y_g) / G) * ((x_r - x_b) * y_w - (x_w - x_b) * y_r + (x_w - x_r) * y_b) / z_w;
                X_G = Y_G * x_g / y_g;
                Z_G = Y_G * (((1 - x_g) / y_g) - 1);
                Y_B = (y_b / B) * ((x_r - x_g) * y_w - (x_w - x_g) * y_r + (x_w - x_r) * y_g) / z_w;
                X_B = Y_B * x_b / y_b;
                Z_B = Y_B * (((1 - x_b) / y_b) - 1);
                X_W = (X_R * R) + (X_G * G) + (X_B * B);
                Y_W = (Y_R * R) + (Y_G * G) + (Y_B * B);
                Z_W = (Z_R * R) + (Z_G * G) + (Z_B * B);
                X_W /= Y_W;
                Z_W /= Y_W;
                Y_W = 1.0f;
            }

            written += writeToFile("<< \n");
            
            string buffer = null;
            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CALGRAY) != 0)
            {
                written += writeToFile("/WhitePoint ");
                buffer = string.Format("[%.4f %.4f %.4f] \n", X_W, Y_W, Z_W);
                written += writeToFile(buffer);
                written += writeToFile("/Gamma 2.2 \n");
            }

            if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_CALRGB) != 0)
            {
                written += writeToFile("/WhitePoint ");
                buffer = string.Format("[%.4f %.4f %.4f] \n", X_W, Y_W, Z_W);
                written += writeToFile(buffer);
                written += writeToFile("/Matrix ");
                buffer = string.Format("[%.4f %.4f %.4f %.4f %.4f %.4f %.4f %.4f %.4f] \n", X_R, Y_R, Z_R, X_G, Y_G, Z_G, X_B, Y_B, Z_B);
                written += writeToFile(buffer);
                written += writeToFile("/Gamma [2.2 2.2 2.2] \n");
            }

            written += writeToFile(">>] \n");
            return written;
        }
        
        /*
        This function writes a PDF Image XObject Colorspace array to output.
        */
        private int write_pdf_xobject_icccs()
        {
            int written = writeToFile("[/ICCBased ");

            string buffer = string.Format("{0}", m_pdf_icccs);
            written += writeToFile(buffer);
            written += writeToFile(" 0 R] \n");

            return written;
        }
        
        private int write_pdf_xobject_icccs_dict()
        {
            int written = writeToFile("/N ");

            string buffer = string.Format("{0} \n", m_tiff_samplesperpixel);
            written += writeToFile(buffer);
            written += writeToFile("/Alternate ");
            m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace ^ t2p_cs_t.T2P_CS_ICCBASED);
            written += write_pdf_xobject_cs();
            m_pdf_colorspace = (t2p_cs_t)(m_pdf_colorspace | t2p_cs_t.T2P_CS_ICCBASED);
            written += write_pdf_stream_dict(m_tiff_iccprofilelength, 0);

            return written;
        }

        private int write_pdf_xobject_icccs_stream()
        {
            return write_pdf_stream(m_tiff_iccprofile, m_tiff_iccprofilelength);
        }
        
        /*
        This function writes a PDF Image XObject Decode array to output.
        */
        private int write_pdf_xobject_decode()
        {
            int written = writeToFile("/Decode [ ");
            for (int i = 0; i < m_tiff_samplesperpixel; i++)
                written += writeToFile("1 0 ");
         
            written += writeToFile("]\n");
            return written;
        }
        
        /*
        This function writes a PDF xref table to output.
        */
        private int write_pdf_xreftable()
        {
            int written = writeToFile("xref\n0 ");
    
            string buffer = string.Format("{0}", m_pdf_xrefcount + 1);
            written += writeToFile(buffer);
            written += writeToFile(" \n0000000000 65535 f \n");
            for (uint i = 0; i < m_pdf_xrefcount; i++)
            {
                buffer = string.Format("%.10lu 00000 n \n", m_pdf_xrefoffsets[i]);
                written += writeToFile(buffer);
            }

            return written;
        }
        
        /*
        * This function writes a PDF trailer to output.
        */
        private int write_pdf_trailer()
        {
            string fileidbuf = "2900000023480000FF180000FF670000";

            m_pdf_fileid = new byte [33];
            for (int i = 0; i < 16; i++)
            {
                m_pdf_fileid[2 * i] = (byte)(fileidbuf[2 * i]);
                m_pdf_fileid[2 * i + 1] = (byte)(fileidbuf[2 * i + 1]);
            }

            int written = writeToFile("trailer\n<<\n/Size ");

            string buffer = string.Format("{0}", m_pdf_xrefcount + 1);
            written += writeToFile(buffer);
            written += writeToFile("\n/Root ");
            buffer = string.Format("{0}", m_pdf_catalog);
            written += writeToFile(buffer);
            written += writeToFile(" 0 R \n/Info ");
            buffer = string.Format("{0}", m_pdf_info);
            written += writeToFile(buffer);
            written += writeToFile(" 0 R \n/ID[<");
            written += writeToFile(m_pdf_fileid, 32);
            written += writeToFile("><");
            written += writeToFile(m_pdf_fileid, 32);
            written += writeToFile(">]\n>>\nstartxref\n");
            buffer = string.Format("{0}", m_pdf_startxref);
            written += writeToFile(buffer);
            written += writeToFile("\n%%EOF\n");

            return written;
        }
        
        /*
        This function writes a PDF Image XObject stream dictionary to output. 
        */
        private int write_pdf_xobject_stream_dict(int tile)
        {
            int written = write_pdf_stream_dict(0, m_pdf_xrefcount + 1);
            written += writeToFile("/Type /XObject \n/Subtype /Image \n/Name /Im");

            string buffer = string.Format("{0}", m_pdf_page + 1);
            written += writeToFile(buffer);
            if (tile != 0)
            {
                written += writeToFile("_");
                buffer = string.Format("{0}", tile);
                written += writeToFile(buffer);
            }

            written += writeToFile("\n/Width ");
            if (tile == 0)
            {
                buffer = string.Format("{0}", m_tiff_width);
            }
            else
            {
                if (tile_is_right_edge(m_tiff_tiles[m_pdf_page], tile - 1))
                    buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_edgetilewidth);
                else
                    buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_tilewidth);
            }

            written += writeToFile(buffer);
            written += writeToFile("\n/Height ");
            if (tile == 0)
            {
                buffer = string.Format("{0}", m_tiff_length);
            }
            else
            {
                if (tile_is_bottom_edge(m_tiff_tiles[m_pdf_page], tile - 1))
                    buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_edgetilelength);
                else
                    buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_tilelength);
            }

            written += writeToFile(buffer);
            written += writeToFile("\n/BitsPerComponent ");
            buffer = string.Format("{0}", m_tiff_bitspersample);
            written += writeToFile(buffer);
            written += writeToFile("\n/ColorSpace ");
            written += write_pdf_xobject_cs();

            if (m_pdf_image_interpolate)
                written += writeToFile("\n/Interpolate true");
            
            if (m_pdf_switchdecode && !(m_pdf_colorspace == t2p_cs_t.T2P_CS_BILEVEL && m_pdf_compression == t2p_compress_t.T2P_COMPRESS_G4))
                written += write_pdf_xobject_decode();

            written += write_pdf_xobject_stream_filter(tile);
            return written;
        }
        
        /*
        This function writes a PDF Image XObject stream filter name and parameters to 
        output.
        */
        private int write_pdf_xobject_stream_filter(int tile)
        {
            if (m_pdf_compression == t2p_compress_t.T2P_COMPRESS_NONE)
                return 0;
         
            int written = writeToFile("/Filter ");
            string buffer = null;

            switch (m_pdf_compression)
            {
                case t2p_compress_t.T2P_COMPRESS_G4:
                    written += writeToFile("/CCITTFaxDecode ");
                    written += writeToFile("/DecodeParms ");
                    written += writeToFile("<< /K -1 ");
                    
                    if (tile == 0)
                    {
                        written += writeToFile("/Columns ");
                        buffer = string.Format("{0}", m_tiff_width);
                        written += writeToFile(buffer);
                        written += writeToFile(" /Rows ");
                        buffer = string.Format("{0}", m_tiff_length);
                        written += writeToFile(buffer);
                    }
                    else
                    {
                        if (!tile_is_right_edge(m_tiff_tiles[m_pdf_page], tile - 1))
                        {
                            written += writeToFile("/Columns ");
                            buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_tilewidth);
                            written += writeToFile(buffer);
                        }
                        else
                        {
                            written += writeToFile("/Columns ");
                            buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_edgetilewidth);
                            written += writeToFile(buffer);
                        }

                        if (!tile_is_bottom_edge(m_tiff_tiles[m_pdf_page], tile - 1))
                        {
                            written += writeToFile(" /Rows ");
                            buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_tilelength);
                            written += writeToFile(buffer);
                        }
                        else
                        {
                            written += writeToFile(" /Rows ");
                            buffer = string.Format("{0}", m_tiff_tiles[m_pdf_page].tiles_edgetilelength);
                            written += writeToFile(buffer);
                        }
                    }

                    if (!m_pdf_switchdecode)
                        written += writeToFile(" /BlackIs1 true ");

                    written += writeToFile(">>\n");
                    break;
                
                case t2p_compress_t.T2P_COMPRESS_JPEG:
                    written += writeToFile("/DCTDecode ");

                    if (m_tiff_photometric != PHOTOMETRIC.PHOTOMETRIC_YCBCR)
                    {
                        written += writeToFile("/DecodeParms ");
                        written += writeToFile("<< /ColorTransform 0 >>\n");
                    }
                    break;

                case t2p_compress_t.T2P_COMPRESS_ZIP:
                    written += writeToFile("/FlateDecode ");
                    if ((m_pdf_defaultcompressionquality % 100) != 0)
                    {
                        written += writeToFile("/DecodeParms ");
                        written += writeToFile("<< /Predictor ");
                        buffer = string.Format("{0}", m_pdf_defaultcompressionquality % 100);
                        written += writeToFile(buffer);
                        written += writeToFile(" /Columns ");
                        buffer = string.Format("{0}", m_tiff_width);
                        written += writeToFile(buffer);
                        written += writeToFile(" /Colors ");
                        buffer = string.Format("{0}", m_tiff_samplesperpixel);
                        written += writeToFile(buffer);
                        written += writeToFile(" /BitsPerComponent ");
                        buffer = string.Format("{0}", m_tiff_bitspersample);
                        written += writeToFile(buffer);
                        written += writeToFile(">>\n");
                    }
                    break;

                default:
                    break;
            }

            return written;
        }
        
        /*
        This function writes a PDF Contents stream to output.
        */
        private int write_pdf_page_content_stream()
        {
            string buffer = null;
            int written = 0;
            if (m_tiff_tiles[m_pdf_page].tiles_tilecount > 0)
            {
                for (uint i = 0; i < m_tiff_tiles[m_pdf_page].tiles_tilecount; i++)
                {
                    T2P_BOX box = m_tiff_tiles[m_pdf_page].tiles_tiles[i].tile_box;
                    buffer = string.Format("q %s %.4f %.4f %.4f %.4f %.4f %.4f cm /Im%d_%ld Do Q\n", 
                        m_tiff_transferfunctioncount != 0 ? "/GS1 gs " : "", box.mat[0], box.mat[1], 
                        box.mat[3], box.mat[4], box.mat[6], box.mat[7], m_pdf_page + 1, i + 1);

                    byte[] bytes = Encoding.ASCII.GetBytes(buffer);
                    written += write_pdf_stream(bytes, bytes.Length);
                }
            }
            else
            {
                T2P_BOX box = m_pdf_imagebox;
                buffer = string.Format("q %s %.4f %.4f %.4f %.4f %.4f %.4f cm /Im%d Do Q\n", 
                    m_tiff_transferfunctioncount != 0 ? "/GS1 gs " : "", box.mat[0], box.mat[1],
                    box.mat[3], box.mat[4], box.mat[6], box.mat[7], m_pdf_page + 1);

                byte[] bytes = Encoding.ASCII.GetBytes(buffer);
                written += write_pdf_stream(bytes, bytes.Length);
            }

            return written;
        }
        
        /*
        This function writes a palette stream for an indexed color space to output.
        */
        private int write_pdf_xobject_palettecs_stream()
        {
            return write_pdf_stream(m_pdf_palette, m_pdf_palettesize);
        }

        /*
        * This functions returns a non-zero value when the tile is on the right edge
        * and does not have full imaged tile width.
        */
        private static bool tile_is_right_edge(T2P_TILES tiles, int tile)
        {
            if (((tile + 1) % tiles.tiles_tilecountx == 0) && (tiles.tiles_edgetilewidth != 0))
                return true;

            return false;
        }

        /*
        * This functions returns a non-zero value when the tile is on the bottom edge
        * and does not have full imaged tile length.
        */
        private static bool tile_is_bottom_edge(T2P_TILES tiles, int tile)
        {
            if (((tile + 1) > (tiles.tiles_tilecount - tiles.tiles_tilecountx)) && (tiles.tiles_edgetilelength != 0))
                return true;

            return false;
        }

        private static bool process_jpeg_strip(byte[] strip, int striplength, byte[] buffer, ref int bufferoffset, int no, int height)
        {
            int i = 1;
            while (i < striplength)
            {
                switch (strip[i])
                {
                    case 0xd8:
                        i += 2;
                        break;

                    case 0xc0:
                    case 0xc1:
                    case 0xc3:
                    case 0xc9:
                    case 0xca:
                        if (no == 0)
                        {
                            Array.Copy(strip, i - 1, buffer, bufferoffset, strip[i + 2] + 2);
                            
                            ushort v_samp = 1;
                            ushort h_samp = 1;
                            for (int j = 0; j < buffer[bufferoffset + 9]; j++)
                            {
                                if ((buffer[bufferoffset + 11 + (2 * j)] >> 4) > h_samp)
                                    h_samp = (ushort)(buffer[bufferoffset + 11 + (2 * j)] >> 4);

                                if ((buffer[bufferoffset + 11 + (2 * j)] & 0x0f) > v_samp)
                                    v_samp = (ushort)(buffer[bufferoffset + 11 + (2 * j)] & 0x0f);
                            }

                            v_samp *= 8;
                            h_samp *= 8;
                            ushort ri = (ushort)(((((ushort)(buffer[bufferoffset + 5]) << 8) | (ushort)buffer[bufferoffset + 6]) + v_samp - 1) / v_samp);
                            ri *= (ushort)(((((ushort)(buffer[bufferoffset + 7]) << 8) | (ushort)buffer[bufferoffset + 8]) + h_samp - 1) / h_samp);
                            buffer[bufferoffset + 5] = (byte)((height >> 8) & 0xff);
                            buffer[bufferoffset + 6] = (byte)(height & 0xff);
                            bufferoffset += strip[i + 2] + 2;
                            i += strip[i + 2] + 2;

                            buffer[bufferoffset++] = 0xff;
                            buffer[bufferoffset++] = 0xdd;
                            buffer[bufferoffset++] = 0x00;
                            buffer[bufferoffset++] = 0x04;
                            buffer[bufferoffset++] = (byte)((ri >> 8) & 0xff);
                            buffer[bufferoffset++] = (byte)(ri & 0xff);
                        }
                        else
                        {
                            i += strip[i + 2] + 2;
                        }
                        break;

                    case 0xc4:
                    case 0xdb:
                        Array.Copy(strip, i - 1, buffer, bufferoffset, strip[i + 2] + 2);
                        bufferoffset += strip[i + 2] + 2;
                        i += strip[i + 2] + 2;
                        break;

                    case 0xda:
                        if (no == 0)
                        {
                            Array.Copy(strip, i - 1, buffer, bufferoffset, strip[i + 2] + 2);
                            bufferoffset += strip[i + 2] + 2;
                            i += strip[i + 2] + 2;
                        }
                        else
                        {
                            buffer[bufferoffset++] = 0xff;
                            buffer[bufferoffset++] = (byte)(0xd0 | ((no - 1) % 8));
                            i += strip[i + 2] + 2;
                        }

                        Array.Copy(strip, i - 1, buffer, bufferoffset, striplength - i - 1);
                        bufferoffset += striplength - i - 1;
                        return true;

                    default:
                        i += strip[i + 2] + 2;
                        break;
                }
            }

            return false;
        }
        
        /*
        * This functions converts in place a buffer of RGBA interleaved data
        * into RGB interleaved data, adding 255-A to each component sample.
        */
        private static int sample_rgba_to_rgb(byte[] data, int samplecount)
        {
            uint[] data32 = Tiff.byteArrayToUInt(data, 0, samplecount * sizeof(uint));

            int i = 0;
            for ( ; i < samplecount; i++)
            {
                uint sample = data32[i];
                byte alpha = (byte)(255 - (sample & 0xff));
                data[i * 3] = (byte)(((sample >> 24) & 0xff) + alpha);
                data[i * 3 + 1] = (byte)(((sample >> 16) & 0xff) + alpha);
                data[i * 3 + 2] = (byte)(((sample >> 8) & 0xff) + alpha);
            }

            return (i * 3);
        }
        
        /*
        * This functions converts in place a buffer of RGBA interleaved data
        * into RGB interleaved data, discarding A.
        */
        private static int sample_rgbaa_to_rgb(byte[] data, int samplecount)
        {
            int i = 0;
            for ( ; i < samplecount; i++)
                Array.Copy(data, i * 4, data, i * 3, 3);

            return (i * 3);
        }
        
        /*
        This functions converts in place a buffer of ABGR interleaved data
        into RGB interleaved data, discarding A.
        */
        private static int sample_abgr_to_rgb(byte[] data, int samplecount)
        {
            uint[] data32 = Tiff.byteArrayToUInt(data, 0, samplecount * sizeof(uint));

            int i = 0;
            for ( ; i < samplecount; i++)
            {
                uint sample = data32[i];
                data[i * 3] = (byte)(sample & 0xff);
                data[i * 3 + 1] = (byte)((sample >> 8) & 0xff);
                data[i * 3 + 2] = (byte)((sample >> 16) & 0xff);
            }

            return (i * 3);
        }
        
        /*
        This function converts the a and b samples of Lab data from signed
        to unsigned.
        */
        private static int sample_lab_signed_to_unsigned(byte[] buffer, int samplecount)
        {
            for (uint i = 0; i < samplecount; i++)
            {
                if ((buffer[i * 3 + 1] & 0x80) != 0)
                    buffer[i * 3 + 1] = (byte)(0x80 + (sbyte)buffer[i * 3 + 1]); // cast to signed int is important
                else
                    buffer[i * 3 + 1] |= 0x80;

                if ((buffer[i * 3 + 2] & 0x80) != 0)
                    buffer[i * 3 + 2] = (byte)(0x80 + (sbyte)buffer[i * 3 + 2]);
                else
                    buffer[i * 3 + 2] |= 0x80;
            }

            return (samplecount * 3);
        }
        
        private static void disable(Tiff tif)
        {
            T2P t2p = tif.Clientdata() as T2P;
            t2p.m_outputdisable = true;
        }

        private static void enable(Tiff tif)
        {
            T2P t2p = tif.Clientdata() as T2P;
            t2p.m_outputdisable = false;
        }

        /*
        This functions converts a tilewidth x tilelength buffer of samples into an edgetilewidth x 
        tilelength buffer of samples.
        */
        private static void tile_collapse_left(byte[] buffer, int scanwidth, int tilewidth, int edgetilewidth, int tilelength)
        {
            int edgescanwidth = (scanwidth * edgetilewidth + tilewidth - 1) / tilewidth;
            for (uint i = 0; i < tilelength; i++)
                Array.Copy(buffer, scanwidth * i, buffer, edgescanwidth * i, edgescanwidth);
        }
        
        /*
        This function writes a PDF string object to output.
        */
        private int write_pdf_string(byte[] pdfstr)
        {
            int written = writeToFile("(");
            int len = strlen(pdfstr);
            for (int i = 0; i < len; i++)
            {
                if ((pdfstr[i] & 0x80) != 0 || (pdfstr[i] == 127) || (pdfstr[i] < 32))
                {
                    string buffer = string.Format("\\%.3hho", pdfstr[i]);
                    written += writeToFile(buffer);
                }
                else
                {
                    switch (pdfstr[i])
                    {
                        case 0x08:
                            written += writeToFile("\\b");
                            break;
                        case 0x09:
                            written += writeToFile("\\t");
                            break;
                        case 0x0A:
                            written += writeToFile("\\n");
                            break;
                        case 0x0C:
                            written += writeToFile("\\f");
                            break;
                        case 0x0D:
                            written += writeToFile("\\r");
                            break;
                        case 0x28:
                            written += writeToFile("\\(");
                            break;
                        case 0x29:
                            written += writeToFile("\\)");
                            break;
                        case 0x5C:
                            written += writeToFile("\\\\");
                            break;
                        default:
                            byte[] b = new byte[1];
                            b[0] = pdfstr[i];
                            written += writeToFile(b, 1);
                            break;
                    }
                }
            }

            written += writeToFile(")");
            return written;
        }
        
        private static void compose_pdf_page_orient(T2P_BOX boxp, ORIENTATION orientation)
        {
            if (boxp.x1 > boxp.x2)
            {
                float f = boxp.x1;
                boxp.x1 = boxp.x2;
                boxp.x2 = f;
            }

            if (boxp.y1 > boxp.y2)
            {
                float f = boxp.y1;
                boxp.y1 = boxp.y2;
                boxp.y2 = f;
            }
            
            float[] m1 = new float[9];
            boxp.mat[0] = m1[0] = boxp.x2 - boxp.x1;
            boxp.mat[1] = m1[1] = 0.0f;
            boxp.mat[2] = m1[2] = 0.0f;
            boxp.mat[3] = m1[3] = 0.0f;
            boxp.mat[4] = m1[4] = boxp.y2 - boxp.y1;
            boxp.mat[5] = m1[5] = 0.0f;
            boxp.mat[6] = m1[6] = boxp.x1;
            boxp.mat[7] = m1[7] = boxp.y1;
            boxp.mat[8] = m1[8] = 1.0f;
            
            switch (orientation)
            {
                case 0:
                case ORIENTATION.ORIENTATION_TOPLEFT:
                    break;

                case ORIENTATION.ORIENTATION_TOPRIGHT:
                    boxp.mat[0] = 0.0F - m1[0];
                    boxp.mat[6] += m1[0];
                    break;

                case ORIENTATION.ORIENTATION_BOTRIGHT:
                    boxp.mat[0] = 0.0F - m1[0];
                    boxp.mat[4] = 0.0F - m1[4];
                    boxp.mat[6] += m1[0];
                    boxp.mat[7] += m1[4];
                    break;

                case ORIENTATION.ORIENTATION_BOTLEFT:
                    boxp.mat[4] = 0.0F - m1[4];
                    boxp.mat[7] += m1[4];
                    break;

                case ORIENTATION.ORIENTATION_LEFTTOP:
                    boxp.mat[0] = 0.0F;
                    boxp.mat[1] = 0.0F - m1[0];
                    boxp.mat[3] = 0.0F - m1[4];
                    boxp.mat[4] = 0.0F;
                    boxp.mat[6] += m1[4];
                    boxp.mat[7] += m1[0];
                    break;

                case ORIENTATION.ORIENTATION_RIGHTTOP:
                    boxp.mat[0] = 0.0F;
                    boxp.mat[1] = 0.0F - m1[0];
                    boxp.mat[3] = m1[4];
                    boxp.mat[4] = 0.0F;
                    boxp.mat[7] += m1[0];
                    break;

                case ORIENTATION.ORIENTATION_RIGHTBOT:
                    boxp.mat[0] = 0.0F;
                    boxp.mat[1] = m1[0];
                    boxp.mat[3] = m1[4];
                    boxp.mat[4] = 0.0F;
                    break;

                case ORIENTATION.ORIENTATION_LEFTBOT:
                    boxp.mat[0] = 0.0F;
                    boxp.mat[1] = m1[0];
                    boxp.mat[3] = 0.0F - m1[4];
                    boxp.mat[4] = 0.0F;
                    boxp.mat[6] += m1[4];
                    break;
            }
        }

        private static void compose_pdf_page_orient_flip(T2P_BOX boxp, ORIENTATION orientation)
        {
            if (boxp.x1 > boxp.x2)
            {
                float f = boxp.x1;
                boxp.x1 = boxp.x2;
                boxp.x2 = f;
            }

            if (boxp.y1 > boxp.y2)
            {
                float f = boxp.y1;
                boxp.y1 = boxp.y2;
                boxp.y2 = f;
            }
            
            float[] m1 = new float[9];
            boxp.mat[0] = m1[0] = boxp.x2 - boxp.x1;
            boxp.mat[1] = m1[1] = 0.0F;
            boxp.mat[2] = m1[2] = 0.0F;
            boxp.mat[3] = m1[3] = 0.0F;
            boxp.mat[4] = m1[4] = boxp.y2 - boxp.y1;
            boxp.mat[5] = m1[5] = 0.0F;
            boxp.mat[6] = m1[6] = boxp.x1;
            boxp.mat[7] = m1[7] = boxp.y1;
            boxp.mat[8] = m1[8] = 1.0F;

            switch (orientation)
            {
            case ORIENTATION.ORIENTATION_LEFTTOP:
                boxp.mat[0] = 0.0F;
                boxp.mat[1] = 0.0F - m1[4];
                boxp.mat[3] = 0.0F - m1[0];
                boxp.mat[4] = 0.0F;
                boxp.mat[6] += m1[0];
                boxp.mat[7] += m1[4];
                break;

            case ORIENTATION.ORIENTATION_RIGHTTOP:
                boxp.mat[0] = 0.0F;
                boxp.mat[1] = 0.0F - m1[4];
                boxp.mat[3] = m1[0];
                boxp.mat[4] = 0.0F;
                boxp.mat[7] += m1[4];
                break;

            case ORIENTATION.ORIENTATION_RIGHTBOT:
                boxp.mat[0] = 0.0F;
                boxp.mat[1] = m1[4];
                boxp.mat[3] = m1[0];
                boxp.mat[4] = 0.0F;
                break;

            case ORIENTATION.ORIENTATION_LEFTBOT:
                boxp.mat[0] = 0.0F;
                boxp.mat[1] = m1[4];
                boxp.mat[3] = 0.0F - m1[0];
                boxp.mat[4] = 0.0F;
                boxp.mat[6] += m1[0];
                break;
            }
        }

        /*
        This function writes a buffer of data to output.
        */
        private int write_pdf_stream(byte[] buffer, int len)
        {
            return writeToFile(buffer, len);
        }
        
        /*
        This functions writes the beginning of a PDF stream to output.
        */
        private int write_pdf_stream_start()
        {
            return writeToFile("stream\n");
        }

        /*
        This function writes the end of a PDF stream to output. 
        */
        private int write_pdf_stream_end()
        {
            return writeToFile("\nendstream\n");
        }
        
        /*
        This function writes a stream dictionary for a PDF stream to output.
        */
        private int write_pdf_stream_dict(int len, int number)
        {
            int written = writeToFile("/Length ");
            if (len != 0)
            {
                written += write_pdf_stream_length(len);
            }
            else
            {
                string buffer = string.Format("{0}", number);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R \n");
            }

            return written;
        }

        /*
        This functions writes the beginning of a PDF stream dictionary to output.
        */
        private int write_pdf_stream_dict_start()
        {
            return writeToFile("<< \n");
        }
        
        /*
        This function writes the end of a PDF stream dictionary to output. 
        */
        private int write_pdf_stream_dict_end()
        {
            return writeToFile(" >>\n");
        }
        
        /*
        This function writes a number to output.
        */
        private int write_pdf_stream_length(int len)
        {
            string buffer = string.Format("{0}", len);
            int written = writeToFile(buffer);
            written += writeToFile("\n");
            return written;
        }

        /*
        This function writes the beginning of a PDF object to output.
        */
        private int write_pdf_obj_start(int number)
        {
            string buffer = string.Format("{0}", number);
            int written = writeToFile(buffer);
            written += writeToFile(" 0 obj\n");
            return written;
        }

        /*
        This function writes the end of a PDF object to output.
        */
        private int write_pdf_obj_end()
        {
            return writeToFile("endobj\n");
        }
        
        /*
        This function writes a PDF Page structure to output.
        */
        private int write_pdf_page(int obj)
        {
            int written = writeToFile("<<\n/Type /Page \n/Parent ");
    
            string buffer = string.Format("{0}", m_pdf_pages);
            written += writeToFile(buffer);
            written += writeToFile(" 0 R \n");
            written += writeToFile("/MediaBox [");
            buffer = string.Format("%.4f", m_pdf_mediabox.x1);
            written += writeToFile(buffer);
            written += writeToFile(" ");
            buffer = string.Format("%.4f", m_pdf_mediabox.y1);
            written += writeToFile(buffer);
            written += writeToFile(" ");
            buffer = string.Format("%.4f", m_pdf_mediabox.x2);
            written += writeToFile(buffer);
            written += writeToFile(" ");
            buffer = string.Format("%.4f", m_pdf_mediabox.y2);
            written += writeToFile(buffer);
            written += writeToFile("] \n");
            written += writeToFile("/Contents ");
            buffer = string.Format("{0}", obj + 1);
            written += writeToFile(buffer);
            written += writeToFile(" 0 R \n");
            written += writeToFile("/Resources << \n");

            if (m_tiff_tiles[m_pdf_page].tiles_tilecount != 0)
            {
                written += writeToFile("/XObject <<\n");
                for (uint i = 0; i < m_tiff_tiles[m_pdf_page].tiles_tilecount; i++)
                {
                    written += writeToFile("/Im");
                    buffer = string.Format("{0}", m_pdf_page + 1);
                    written += writeToFile(buffer);
                    written += writeToFile("_");
                    buffer = string.Format("{0}", i + 1);
                    written += writeToFile(buffer);
                    written += writeToFile(" ");
                    buffer = string.Format("{0}", obj + 3 + 2 * i + m_tiff_pages[m_pdf_page].page_extra);
                    written += writeToFile(buffer);
                    written += writeToFile(" 0 R ");
                    if (i % 4 == 3)
                        written += writeToFile("\n");
                }
                
                written += writeToFile(">>\n");
            }
            else
            {
                written += writeToFile("/XObject <<\n");
                written += writeToFile("/Im");
                buffer = string.Format("{0}", m_pdf_page + 1);
                written += writeToFile(buffer);
                written += writeToFile(" ");
                buffer = string.Format("{0}", obj + 3 + m_tiff_pages[m_pdf_page].page_extra);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");
                written += writeToFile(">>\n");
            }

            if (m_tiff_transferfunctioncount != 0)
            {
                written += writeToFile("/ExtGState <<");
                writeToFile("/GS1 ");
                buffer = string.Format("{0}", obj + 3);
                written += writeToFile(buffer);
                written += writeToFile(" 0 R ");
                written += writeToFile(">> \n");
            }

            written += writeToFile("/ProcSet [ ");
            if (m_pdf_colorspace == t2p_cs_t.T2P_CS_BILEVEL || m_pdf_colorspace == t2p_cs_t.T2P_CS_GRAY)
            {
                written += writeToFile("/ImageB ");
            }
            else
            {
                written += writeToFile("/ImageC ");
                if ((m_pdf_colorspace & t2p_cs_t.T2P_CS_PALETTE) != 0)
                    written += writeToFile("/ImageI ");
            }

            written += writeToFile("]\n>>\n>>\n");
            return written;
        }

        private int writeToFile(byte[] data, int size)
        {
            if (data == null || size == 0)
                return 0;

            TiffStream stream = m_output.GetStream();
            if (stream != null)
            {
                object client = m_output.Clientdata();
                stream.Write(client, data, size);
                return size;
            }

            return -1;
        }

        private int writeToFile(string data)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(data);
            return writeToFile(bytes, bytes.Length);
        }
    }
}
