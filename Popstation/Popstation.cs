﻿using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Popstation
{
    public enum PopstationEventEnum
    {
        WritePbpHeader,
        WriteSfo,
        WriteHeader,
        WriteBootPng,
        WriteIndex,
        WriteIso,
        WriteIcon0Png,
        WriteIcon1Pmf,
        WritePic0Png,
        WritePic1Png,
        WriteSnd0At3,
        WriteDataPsp,
        WritePsTitle,
        WriteIsoHeader,
        WriteProgress,
        UpdateIndex,
        WriteSpecialData
    }

    public partial class Popstation
    {
        public Action<PopstationEventEnum, object> OnEvent { get; set; }

        ConvertIsoInfo convertInfo;
        bool cancelConvert;
        int nextPatchPos;

        public void Convert(ConvertIsoInfo info)
        {
            convertInfo = info;
            cancelConvert = false;

            if (convertInfo.patchCount > 0)
            {
                nextPatchPos = 0;
            }

            if (convertInfo.multiDiscInfo.fileCount == 1)
            {
                ConvertIso();
            }
            else
            {
                ConvertIsoMD();
            }
        }

        private void PatchData(byte[] buffer, int size, int pos)
        {
            while (true)
            {
                if (nextPatchPos >= convertInfo.patchCount) break;
                if ((pos <= convertInfo.patchData[nextPatchPos].dataPosition) && ((pos + size) >= convertInfo.patchData[nextPatchPos].dataPosition))
                {
                    buffer[convertInfo.patchData[nextPatchPos].dataPosition - pos] = convertInfo.patchData[nextPatchPos].newData;
                    nextPatchPos++;
                }
                else break;
            }
        }

        private byte[] Compress(byte[] inbuf, int level)
        {
            using (var ms = new MemoryStream())
            {
                var deflater = new Deflater(level, true);
                using (var outStream = new DeflaterOutputStream(ms, deflater))
                {
                    outStream.Write(inbuf, 0, inbuf.Length);
                    outStream.Flush();
                    outStream.Finish();
                    return ms.ToArray();
                }
            }
        }

        void SetSFOTitle(byte[] sfo, string title)
        {
            SFOHeader header = new SFOHeader();
            header.signature = BitConverter.ToUInt32(sfo, 0);
            header.version = BitConverter.ToUInt32(sfo, 4);
            header.fields_table_offs = BitConverter.ToUInt32(sfo, 8);
            header.values_table_offs = BitConverter.ToUInt32(sfo, 12);
            header.nitems = BitConverter.ToInt32(sfo, 16);

            SFODir[] entries = new SFODir[header.nitems];
            var offset = 0x14;

            for (var i = 0; i < header.nitems; i++)
            {

                entries[i] = new SFODir()
                {
                    field_offs = BitConverter.ToUInt16(sfo, offset + 0),
                    unk = sfo[offset + 2],
                    type = sfo[offset + 3],
                    length = BitConverter.ToUInt32(sfo, offset + 4),
                    size = BitConverter.ToUInt32(sfo, offset + 8),
                    val_offs = BitConverter.ToUInt16(sfo, offset + 12),
                    unk4 = BitConverter.ToUInt16(sfo, offset + 16),
                };

                offset += 16;
            }

            for (var i = 0; i < header.nitems; i++)
            {
                var text = Encoding.ASCII.GetString(sfo, (int)(header.fields_table_offs + entries[i].field_offs), 5);

                if (text == "TITLE")
                {
                    Array.Clear(sfo, (int)header.values_table_offs + entries[i].val_offs, (int)entries[i].size);
                    var titleBytes = Encoding.ASCII.GetBytes(title);
                    Array.Copy(titleBytes, 0, sfo, (int)header.values_table_offs + entries[i].val_offs, titleBytes.Length);

                    if (title.Length + 1 > entries[i].size)
                    {
                        entries[i].length = entries[i].size;
                    }
                    else
                    {
                        entries[i].length = (uint)(title.Length + 1);
                    }
                }
            }
        }

        private void SetSFOCode(byte[] sfo, string code)
        {
            SFOHeader header = new SFOHeader();
            header.signature = BitConverter.ToUInt32(sfo, 0);
            header.version = BitConverter.ToUInt32(sfo, 4);
            header.fields_table_offs = BitConverter.ToUInt32(sfo, 8);
            header.values_table_offs = BitConverter.ToUInt32(sfo, 12);
            header.nitems = BitConverter.ToInt32(sfo, 16);

            SFODir[] entries = new SFODir[header.nitems];
            var offset = 0x14;

            for (var i = 0; i < header.nitems; i++)
            {

                entries[i] = new SFODir()
                {
                    field_offs = BitConverter.ToUInt16(sfo, offset + 0),
                    unk = sfo[offset + 2],
                    type = sfo[offset + 3],
                    length = BitConverter.ToUInt32(sfo, offset + 4),
                    size = BitConverter.ToUInt32(sfo, offset + 8),
                    val_offs = BitConverter.ToUInt16(sfo, offset + 12),
                    unk4 = BitConverter.ToUInt16(sfo, offset + 16),
                };

                offset += 16;
            }

            for (var i = 0; i < header.nitems; i++)
            {
                var text = Encoding.ASCII.GetString(sfo, (int)(header.fields_table_offs + entries[i].field_offs), 7);

                if (text == "DISC_ID")
                {
                    Array.Clear(sfo, (int)header.values_table_offs + entries[i].val_offs, (int)entries[i].size);
                    var titleBytes = Encoding.ASCII.GetBytes(code);
                    Array.Copy(titleBytes, 0, sfo, (int)header.values_table_offs + entries[i].val_offs, titleBytes.Length);

                    if (code.Length + 1 > entries[i].size)
                    {
                        entries[i].length = entries[i].size;
                    }
                    else
                    {
                        entries[i].length = (uint)(code.Length + 1);
                    }
                }
            }
        }


        public void ConvertIsoMD()
        {
            byte[] buffer = new byte[1 * 1048576];
            byte[] buffer2 = new byte[0x9300];
            uint totSize;

            bool pic0 = false, pic1 = false, icon0 = false, icon1 = false, snd = false, toc = false, boot = false, prx = false;
            uint sfo_size, pic0_size, pic1_size, icon0_size, icon1_size, snd_size, toc_size, boot_size, prx_size;

            boot_size = 0;

            uint[] psp_header = new uint[0x30 / 4];
            uint[] base_header = new uint[0x28 / 4];
            uint[] header = new uint[0x28 / 4];
            uint[] dummy = new uint[6];

            uint i, offset, isosize, isorealsize, x;
            uint index_offset, p1_offset, p2_offset, m_offset, end_offset;
            IsoIndex[] indexes = null;
            uint[] iso_positions = new uint[5];
            int ciso;
            //z_stream z;
            end_offset = 0;

            int ndiscs;
            string[] inputs = new string[4];
            string output;
            string title;
            string[] titles = new string[4];
            string code;
            string[] codes = new string[4];
            int[] complevels = new int[4];
            string _sbase;

            ndiscs = convertInfo.multiDiscInfo.fileCount;

            inputs[0] = convertInfo.multiDiscInfo.srcISO1;
            inputs[1] = convertInfo.multiDiscInfo.srcISO2;
            inputs[2] = convertInfo.multiDiscInfo.srcISO3;
            inputs[3] = convertInfo.multiDiscInfo.srcISO4;

            titles[0] = convertInfo.multiDiscInfo.gameTitle1;
            titles[1] = convertInfo.multiDiscInfo.gameTitle2;
            titles[2] = convertInfo.multiDiscInfo.gameTitle3;
            titles[3] = convertInfo.multiDiscInfo.gameTitle4;

            codes[0] = convertInfo.multiDiscInfo.gameID1;
            codes[1] = convertInfo.multiDiscInfo.gameID2;
            codes[2] = convertInfo.multiDiscInfo.gameID3;
            codes[3] = convertInfo.multiDiscInfo.gameID4;

            complevels[0] = convertInfo.compLevel;
            complevels[1] = convertInfo.compLevel;
            complevels[2] = convertInfo.compLevel;
            complevels[3] = convertInfo.compLevel;

            output = convertInfo.dstPBP;
            title = convertInfo.gameTitle;
            code = convertInfo.gameID;
            _sbase = convertInfo._base;

            using (var _base = new FileStream(_sbase, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var _out = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Write))
                {
                    OnEvent?.Invoke(PopstationEventEnum.WritePbpHeader, null);

                    _base.Read(base_header, 1, 0x28);

                    if (base_header[0] != 0x50425000)
                    {
                        throw new Exception($"{_base} is not a PBP file.");
                    }

                    sfo_size = base_header[3] - base_header[2];

                    if (File.Exists(convertInfo.icon0))
                    {
                        var t = new FileInfo(convertInfo.icon0);
                        icon0_size = (uint)t.Length;
                        icon0 = true;
                    }
                    else
                    {
                        icon0_size = base_header[4] - base_header[3];
                    }

                    if (File.Exists(convertInfo.icon1))
                    {
                        var t = new FileInfo(convertInfo.icon1);
                        icon1_size = (uint)t.Length;
                        icon1 = true;
                    }
                    else
                    {
                        icon1_size = 0;
                    }

                    if (File.Exists(convertInfo.pic0))
                    {
                        var t = new FileInfo(convertInfo.pic0);
                        pic0_size = (uint)t.Length;
                        pic0 = true;
                    }
                    else
                    {
                        pic0_size = 0; //base_header[6] - base_header[5];
                    }


                    if (File.Exists(convertInfo.pic1))
                    {
                        var t = new FileInfo(convertInfo.pic1);
                        pic1_size = (uint)t.Length;
                        pic1 = true;
                    }
                    else
                    {
                        pic1_size = 0; // base_header[7] - base_header[6];
                    }


                    if (File.Exists(convertInfo.snd0))
                    {
                        var t = new FileInfo(convertInfo.snd0);
                        snd_size = (uint)t.Length;
                        snd = true;
                    }
                    else
                    {
                        snd_size = 0;
                    }

                    if (File.Exists(convertInfo.boot))
                    {
                        var t = new FileInfo(convertInfo.boot);
                        boot_size = (uint)t.Length;
                        boot = true;
                    }
                    else
                    {
                        //boot = false;
                    }

                    if (File.Exists(convertInfo.data_psp))
                    {
                        var t = new FileInfo(convertInfo.data_psp);
                        prx_size = (uint)t.Length;
                        prx = true;
                    }
                    else
                    {
                        _base.Seek(base_header[8], SeekOrigin.Begin);
                        _base.Read(psp_header, 1, 0x30);

                        prx_size = psp_header[0x2C / 4];
                    }

                    uint curoffs = 0x28;

                    header[0] = 0x50425000;
                    header[1] = 0x10000;

                    header[2] = curoffs;

                    curoffs += sfo_size;
                    header[3] = curoffs;

                    curoffs += icon0_size;
                    header[4] = curoffs;

                    curoffs += icon1_size;
                    header[5] = curoffs;

                    curoffs += pic0_size;
                    header[6] = curoffs;

                    curoffs += pic1_size;
                    header[7] = curoffs;

                    curoffs += snd_size;
                    header[8] = curoffs;

                    x = header[8] + prx_size;

                    if ((x % 0x10000) != 0)
                    {
                        x = x + (0x10000 - (x % 0x10000));
                    }

                    header[9] = x;

                    _out.Write(header, 0, 0x28);

                    OnEvent?.Invoke(PopstationEventEnum.WriteSfo, null);

                    _base.Seek(base_header[2], SeekOrigin.Begin);
                    _base.Read(buffer, 0, (int)sfo_size);
                    SetSFOTitle(buffer, title);
                    //strcpy(buffer + 0x108, code);
                    var codeBytes = Encoding.ASCII.GetBytes(code);
                    Array.Copy(codeBytes, 0, buffer, 0x108, codeBytes.Length);


                    _out.Write(buffer, 0, (int)sfo_size);

                    OnEvent?.Invoke(PopstationEventEnum.WriteIcon0Png, null);

                    if (!icon0)
                    {
                        _base.Seek(base_header[3], SeekOrigin.Begin);
                        _base.Read(buffer, 0, (int)icon0_size);
                        _out.Write(buffer, 0, (int)icon0_size);
                    }
                    else
                    {
                        using (var t = new FileStream(convertInfo.icon0, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)icon0_size);
                            _out.Write(buffer, 0, (int)icon0_size);
                        }
                    }

                    if (icon1)
                    {
                        OnEvent?.Invoke(PopstationEventEnum.WriteIcon1Pmf, null);

                        using (var t = new FileStream(convertInfo.icon1, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)icon1_size);
                            _out.Write(buffer, 0, (int)icon0_size);
                        }
                    }

                    if (!pic0)
                    {
                        //_base.Seek(base_header[5], SeekOrigin.Begin);
                        //_base.Read(buffer, 1, pic0_size);
                        //_out.Write(buffer, 1, pic0_size);
                    }
                    else
                    {
                        OnEvent?.Invoke(PopstationEventEnum.WritePic0Png, null);

                        using (var t = new FileStream(convertInfo.pic0, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)pic0_size);
                            _out.Write(buffer, 0, (int)pic0_size);
                        }
                    }

                    if (!pic1)
                    {
                        //_base.Seek(base_header[6], SeekOrigin.Begin);
                        //_base.Read(buffer, 0, pic1_size);
                        //_out.Write(buffer, 0, pic1_size);		
                    }
                    else
                    {
                        OnEvent?.Invoke(PopstationEventEnum.WritePic1Png, null);

                        using (var t = new FileStream(convertInfo.pic1, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)pic1_size);
                            _out.Write(buffer, 0, (int)pic1_size);
                        }
                    }

                    if (snd)
                    {
                        OnEvent?.Invoke(PopstationEventEnum.WriteSnd0At3, null);

                        using (var t = new FileStream(convertInfo.snd0, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)snd_size);
                            _out.Write(buffer, 0, (int)snd_size);
                        }
                    }

                    OnEvent?.Invoke(PopstationEventEnum.WriteDataPsp, null);

                    if (prx)
                    {
                        using (var t = new FileStream(convertInfo.data_psp, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)prx_size);
                            _out.Write(buffer, 0, (int)prx_size);
                        }
                    }
                    else
                    {
                        _base.Seek(base_header[8], SeekOrigin.Begin);
                        _base.Read(buffer, 0, (int)prx_size);
                        _out.Write(buffer, 0, (int)prx_size);
                    }

                    offset = (uint)_out.Position;

                    for (i = 0; i < header[9] - offset; i++)
                    {
                        _out.WriteByte(0);
                    }

                    OnEvent?.Invoke(PopstationEventEnum.WritePsTitle, null);

                    _out.Write("PSTITLEIMG000000", 0, 16);

                    // Save this offset position
                    p1_offset = (uint)_out.Position;

                    _out.WriteInteger(0, 2);
                    _out.WriteInteger(0x2CC9C5BC, 1);
                    _out.WriteInteger(0x33B5A90F, 1);
                    _out.WriteInteger(0x06F6B4B3, 1);
                    _out.WriteInteger(0xB25945BA, 1);
                    _out.WriteInteger(0, 0x76);

                    m_offset = (uint)_out.Position;

                    //memset(iso_positions, 0, sizeof(iso_positions));
                    _out.Write(iso_positions, 1, sizeof(uint) * 5);

                    _out.WriteRandom(12);
                    _out.WriteInteger(0, 8);

                    _out.Write('_');
                    _out.Write(code, 0, 4);
                    _out.Write('_');
                    _out.Write(code, 4, 5);

                    _out.WriteChar(0, 0x15);

                    p2_offset = (uint)_out.Position;
                    _out.WriteInteger(0, 2);

                    _out.Write(data3, 0, data3.Length);
                    _out.Write(title, 0, title.Length);

                    _out.WriteChar(0, 0x80 - title.Length);
                    _out.WriteInteger(7, 1);
                    _out.WriteInteger(0, 0x1C);

                    Stream _in;
                    //Get size of all isos
                    totSize = 0;
                    for (ciso = 0; ciso < ndiscs; ciso++)
                    {
                        if (File.Exists(inputs[ciso]))
                        {
                            var t = new FileInfo(inputs[ciso]);
                            isosize = (uint)t.Length;
                            totSize += isosize;
                        }
                    }

                    //TODO: Callback
                    //PostMessage(convertInfo.callback, WM_CONVERT_SIZE, 0, totSize);

                    totSize = 0;
                    var lastTicks = DateTime.Now.Ticks;

                    for (ciso = 0; ciso < ndiscs; ciso++)
                    {
                        if (!File.Exists(inputs[ciso]))
                        {
                            continue;
                        }

                        using (_in = new FileStream(inputs[ciso], FileMode.Open, FileAccess.Read))
                        {

                            var t = new FileInfo(inputs[ciso]);
                            isosize = (uint)t.Length;
                            isorealsize = isosize;

                            if ((isosize % 0x9300) != 0)
                            {
                                isosize = isosize + (0x9300 - (isosize % 0x9300));
                            }

                            offset = (uint)_out.Position;

                            if (offset % 0x8000 == 0)
                            {
                                x = 0x8000 - (offset % 0x8000);
                                _out.WriteChar(0, (int)x);
                            }

                            iso_positions[ciso] = (uint)_out.Position - header[9];

                            OnEvent?.Invoke(PopstationEventEnum.WriteIsoHeader, ciso + 1);

                            _out.Write("PSISOIMG0000", 0, 12);

                            _out.WriteInteger(0, 0xFD);

                            //TODO??
                            var titleBytes = Encoding.ASCII.GetBytes(codes[ciso]);
                            Array.Copy(titleBytes, 0, data1, 1, 4);

                            titleBytes = Encoding.ASCII.GetBytes(codes[ciso]);
                            Array.Copy(titleBytes, 4, data1, 6, 5);

                            //memcpy(data1, 1, codes[ciso], 4);
                            //memcpy(data1, 6, codes[ciso] + 4, 5);
                            _out.Write(data1, 0, data1.Length);

                            _out.WriteInteger(0, 1);

                            //TODO:
                            titleBytes = Encoding.ASCII.GetBytes(titles[ciso]);
                            Array.Copy(titleBytes, 0, data2, 8, titles[ciso].Length);
                            //strcpy((char*)(data2 + 8), titles[ciso]);
                            _out.Write(data2, 0, data2.Length);

                            index_offset = (uint)_out.Position;

                            Console.WriteLine("Writing indexes (iso #%d)...\n", ciso + 1);
                            OnEvent?.Invoke(PopstationEventEnum.WriteIndex, ciso + 1);

                            //memset(dummy, 0, sizeof(dummy));

                            offset = 0;

                            if (complevels[ciso] == 0)
                            {
                                x = 0x9300;
                            }
                            else
                            {
                                x = 0;
                            }

                            for (i = 0; i < isosize / 0x9300; i++)
                            {
                                _out.WriteInteger(offset, 1);
                                _out.WriteInteger(x, 1);
                                _out.Write(dummy, 0, sizeof(uint) * dummy.Length);

                                if (complevels[ciso] == 0)
                                    offset += 0x9300;
                            }

                            offset = (uint)_out.Position;

                            for (i = 0; i < (iso_positions[ciso] + header[9] + 0x100000) - offset; i++)
                            {
                                _out.WriteByte(0);
                            }

                            //Console.WriteLine("Writing iso #%d (%s)...\n", ciso + 1, inputs[ciso]);
                            OnEvent?.Invoke(PopstationEventEnum.WriteIso, ciso + 1);

                            if (complevels[ciso] == 0)
                            {
                                while ((x = (uint)_in.Read(buffer, 0, 1048576)) > 0)
                                {
                                    _out.Write(buffer, 0, (int)x);
                                    totSize += x;
                                    // PostMessage(convertInfo.callback, WM_CONVERT_PROGRESS, 0, totSize);
                                    if (cancelConvert)
                                    {
                                        return;
                                    }
                                }

                                for (i = 0; i < (isosize - isorealsize); i++)
                                {
                                    _out.WriteByte(0);
                                }
                            }
                            else
                            {
                                indexes = new IsoIndex[(isosize / 0x9300)];

                                //if (!indexes)
                                //{
                                //    fclose(_in);
                                //    fclose(_out);
                                //    fclose(_base);

                                //     throw new Exception("Cannot alloc memory for indexes!\n");
                                //}

                                i = 0;
                                offset = 0;

                                while ((x = (uint)_in.Read(buffer2, 0, 0x9300)) > 0)
                                {
                                    totSize += x;

                                    if (x < 0x9300)
                                    {
                                        for (var j = 0; j < 0x9300 - x; j++)
                                        {
                                            buffer2[j + x] = 0;
                                        }
                                        //memset(buffer2 + x, 0, 0x9300 - x);
                                    }

                                    var cbuffer = Compress(buffer2, complevels[ciso]);

                                    //if (bytesc < 0)
                                    //{
                                    //    //fclose(_in);
                                    //    //fclose(_out);
                                    //    //fclose(_base);
                                    //    //free(indexes);

                                    //    throw new Exception("Error _in compression!");
                                    //}

                                    x = (uint)cbuffer.Length;

                                    //memset(&indexes[i], 0, sizeof(IsoIndex));
                                    indexes[i] = new IsoIndex();
                                    indexes[i].offset = offset;

                                    if (x >= 0x9300) /* Block didn't compress */
                                    {
                                        indexes[i].length = 0x9300;
                                        _out.Write(buffer2, 0, 0x9300);
                                        offset += 0x9300;
                                    }
                                    else
                                    {
                                        indexes[i].length = x;
                                        _out.Write(cbuffer, 0, (int)x);
                                        offset += x;
                                    }

                                    // PostMessage(convertInfo.callback, WM_CONVERT_PROGRESS, 0, totSize);
                                    OnEvent?.Invoke(PopstationEventEnum.WriteProgress, ciso + 1);

                                    //if (DateTime.Now.Ticks - lastTicks > 500000)
                                    //{
                                    //    //Console.WriteLine($"{totSize} of {isosize} - {Math.Round((double)totSize / isosize * 100, 0)}%");
                                    //    lastTicks = DateTime.Now.Ticks;
                                    //}

                                    if (cancelConvert)
                                    {
                                        return;
                                    }

                                    i++;
                                }

                                if (i != (isosize / 0x9300))
                                {
                                    throw new Exception("Some error happened.\n");
                                }

                            }
                        }

                        if (complevels[ciso] != 0)
                        {
                            offset = (uint)_out.Position;

                            //Console.WriteLine($"Updating compressed indexes (iso {ciso + 1})...");
                            OnEvent?.Invoke(PopstationEventEnum.UpdateIndex, ciso + 1);

                            _out.Seek(index_offset, SeekOrigin.Begin);
                            //TODO: 
                            _out.Write(indexes, 0, (int)(4 + 4 + (6 * 4) * (isosize / 0x9300)));

                            _out.Seek(offset, SeekOrigin.Begin);
                        }
                    }

                    x = (uint)_out.Position;

                    if ((x % 0x10) != 0)
                    {
                        end_offset = x + (0x10 - (x % 0x10));

                        for (i = 0; i < (end_offset - x); i++)
                        {
                            _out.Write('0');
                        }
                    }
                    else
                    {
                        end_offset = x;
                    }

                    end_offset -= header[9];

                    Console.WriteLine("Writing special data...\n");
                    OnEvent?.Invoke(PopstationEventEnum.WriteSpecialData, null);

                    _base.Seek(base_header[9] + 12, SeekOrigin.Begin);
                    var temp = new byte[sizeof(uint)];
                    _base.Read(temp, 0, 4);
                    x = BitConverter.ToUInt32(temp, 0);

                    x += 0x50000;

                    _base.Seek(x, SeekOrigin.Begin);
                    _base.Read(buffer, 0, 8);

                    var tempstr = System.Text.Encoding.ASCII.GetString(buffer, 0, 8);

                    if (tempstr != "STARTDAT")
                    {
                        throw new Exception($"Cannot find STARTDAT _in {_sbase}. Not a valid PSX eboot.pbp");
                    }

                    _base.Seek(x + 16, SeekOrigin.Begin);
                    _base.Read(header, 0, 8);
                    _base.Seek(x, SeekOrigin.Begin);
                    _base.Read(buffer, 0, (int)header[0]);

                    if (!boot)
                    {
                        _out.Write(buffer, 0, (int)header[0]);
                        _base.Read(buffer, 0, (int)header[1]);
                        _out.Write(buffer, 0, (int)header[1]);
                    }
                    else
                    {
                        Console.WriteLine("Writing boot.png...\n");
                        OnEvent?.Invoke(PopstationEventEnum.WriteBootPng, null);

                        //ib[5] = boot_size;
                        var temp_buffer = BitConverter.GetBytes(boot_size);
                        for (var j = 0; j < sizeof(uint); j++)
                        {
                            buffer[5 + j] = temp_buffer[i];
                        }

                        _out.Write(buffer, 0, (int)header[0]);

                        using (var t = new FileStream(convertInfo.boot, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            t.Read(buffer, 0, (int)boot_size);
                            _out.Write(buffer, 0, (int)boot_size);
                        }

                        _base.Read(buffer, 0, (int)header[1]);
                    }

                    //_base.Seek(x, SeekOrigin.Begin);

                    while ((x = (uint)_base.Read(buffer, 0, 1048576)) > 0)
                    {
                        _out.Write(buffer, 0, (int)x);
                    }

                    _out.Seek(p1_offset, SeekOrigin.Begin);
                    _out.WriteInteger(end_offset, 1);

                    end_offset += 0x2d31;
                    _out.Seek(p2_offset, SeekOrigin.Begin);
                    _out.WriteInteger(end_offset, 1);

                    _out.Seek(m_offset, SeekOrigin.Begin);
                    _out.Write(iso_positions, 1, sizeof(uint) * iso_positions.Length);

                }
            }
        }

        public void ConvertIso()
        {
            //FILE *_in, *_out, *_base, *t;
            uint i, j, offset, isosize, isorealsize, x;
            uint index_offset, p1_offset, p2_offset, end_offset;
            IsoIndex[] indexes = null;
            uint curoffs = 0x28;
            string input;
            string output;
            string title;
            string code;
            string _sbase;

            end_offset = 0;

            int complevel;

            byte[] buffer = new byte[1 * 1048576];
            byte[] buffer2 = new byte[0x9300];
            uint totSize;

            bool pic0 = false, pic1 = false, icon0 = false, icon1 = false, snd = false, toc = false, boot = false, prx = false;
            uint sfo_size, pic0_size, pic1_size, icon0_size, icon1_size, snd_size, toc_size, boot_size, prx_size;

            boot_size = 0;

            uint[] psp_header = new uint[0x30 / 4];
            uint[] base_header = new uint[0x28 / 4];
            uint[] header = new uint[0x28 / 4];
            uint[] dummy = new uint[6];

            int blockCount = 0;
            //uint i, offset, isosize, isorealsize, x;
            //uint index_offset, p1_offset, p2_offset, m_offset, end_offset;


            _sbase = convertInfo._base;
            input = convertInfo.srcISO;
            output = convertInfo.dstPBP;
            //	title=convertInfo.title;
            //	code=convertInfo.gameCode;
            complevel = convertInfo.compLevel;


            var iso_index = new List<INDEX>();

            //_in = fopen(input, "rb");
            using (var _in = new FileStream(input, FileMode.Open, FileAccess.Read))
            {

                //if (!_in)
                //{
                //    if (input[0] == 0)
                //        throw new Exception("No input file selected.");
                //    else
                //        throw new Exception("Unable to open \"%s\".", input);
                //}

                //Check if input is pbp
                if (convertInfo.srcIsPbp)
                {
                    iso_index = Init(convertInfo.srcISO);
                    blockCount = iso_index.Count;
                    if (iso_index.Count == 0) throw new Exception("No iso index was found.");
                    isosize = (uint)GetIsoSize(iso_index);
                }
                else
                {
                    _in.Seek(0, SeekOrigin.End);
                    isosize = (uint)_in.Position;
                    _in.Seek(0, SeekOrigin.Begin);
                }
                isorealsize = isosize;

                //PostMessage(convertInfo.callback, WM_CONVERT_SIZE, 0, isosize);

                if ((isosize % 0x9300) != 0)
                {
                    isosize = isosize + (0x9300 - (isosize % 0x9300));
                }

                //Console.WriteLine("isosize, isorealsize %08X  %08X\n", isosize, isorealsize);

                using (var _base = new FileStream(_sbase, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var _out = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Write))
                    {

                        Console.WriteLine("Writing header...\n");
                        OnEvent?.Invoke(PopstationEventEnum.WriteHeader, null);

                        _base.Read(base_header, 1, 0x28);

                        if (base_header[0] != 0x50425000)
                        {
                            throw new Exception($"{_sbase} is not a PBP file.");
                        }

                        sfo_size = base_header[3] - base_header[2];

                        if (File.Exists(convertInfo.icon0))
                        {
                            var t = new FileInfo(convertInfo.icon0);
                            icon0_size = (uint)t.Length;
                            icon0 = true;
                        }
                        else
                        {
                            icon0_size = base_header[4] - base_header[3];
                        }

                        if (File.Exists(convertInfo.icon1))
                        {
                            var t = new FileInfo(convertInfo.icon1);
                            icon1_size = (uint)t.Length;
                            icon1 = true;
                        }
                        else
                        {
                            icon1_size = 0;
                        }

                        if (File.Exists(convertInfo.pic0))
                        {
                            var t = new FileInfo(convertInfo.pic0);
                            pic0_size = (uint)t.Length;
                            pic0 = true;
                        }
                        else
                        {
                            pic0_size = 0; //base_header[6] - base_header[5];
                        }


                        if (File.Exists(convertInfo.pic1))
                        {
                            var t = new FileInfo(convertInfo.pic1);
                            pic1_size = (uint)t.Length;
                            pic1 = true;
                        }
                        else
                        {
                            pic1_size = 0; // base_header[7] - base_header[6];
                        }


                        if (File.Exists(convertInfo.snd0))
                        {
                            var t = new FileInfo(convertInfo.snd0);
                            snd_size = (uint)t.Length;
                            snd = true;
                        }
                        else
                        {
                            snd_size = 0;
                        }

                        if (File.Exists(convertInfo.boot))
                        {
                            var t = new FileInfo(convertInfo.boot);
                            boot_size = (uint)t.Length;
                            boot = true;
                        }
                        else
                        {
                            //boot = false;
                        }

                        if (File.Exists(convertInfo.data_psp))
                        {
                            var t = new FileInfo(convertInfo.data_psp);
                            prx_size = (uint)t.Length;
                            prx = true;
                        }
                        else
                        {
                            _base.Seek(base_header[8], SeekOrigin.Begin);
                            _base.Read(psp_header, 1, 0x30);

                            prx_size = psp_header[0x2C / 4];
                        }


                        header[0] = 0x50425000;
                        header[1] = 0x10000;

                        header[2] = curoffs;

                        curoffs += sfo_size;
                        header[3] = curoffs;

                        curoffs += icon0_size;
                        header[4] = curoffs;

                        curoffs += icon1_size;
                        header[5] = curoffs;

                        curoffs += pic0_size;
                        header[6] = curoffs;

                        curoffs += pic1_size;
                        header[7] = curoffs;

                        curoffs += snd_size;
                        header[8] = curoffs;

                        x = header[8] + prx_size;

                        if ((x % 0x10000) != 0)
                        {
                            x = x + (0x10000 - (x % 0x10000));
                        }

                        header[9] = x;

                        _out.Write(header, 0, 0x28);

                        Console.WriteLine("Writing sfo...\n");

                        _base.Seek(base_header[2], SeekOrigin.Begin);
                        _base.Read(buffer, 0, (int)sfo_size);

                        SetSFOTitle(buffer, convertInfo.saveTitle);
                        SetSFOCode(buffer, convertInfo.saveID);
                        // AS IS
                        //strcpy(buffer+0x108, code);

                        _out.Write(buffer, 0, (int)sfo_size);

                        Console.WriteLine("Writing icon0.png...\n");

                        if (!icon0)
                        {
                            _base.Seek(base_header[3], SeekOrigin.Begin);
                            _base.Read(buffer, 0, (int)icon0_size);
                            _out.Write(buffer, 0, (int)icon0_size);
                        }
                        else
                        {
                            using (var t = new FileStream(convertInfo.icon0, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)icon0_size);
                                _out.Write(buffer, 0, (int)icon0_size);
                            }
                        }

                        if (icon1)
                        {
                            Console.WriteLine("Writing icon1.pmf...\n");

                            using (var t = new FileStream(convertInfo.icon1, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)icon1_size);
                                _out.Write(buffer, 0, (int)icon0_size);
                            }
                        }

                        if (!pic0)
                        {
                            //_base.Seek(base_header[5], SeekOrigin.Begin);
                            //_base.Read(buffer, 1, pic0_size);
                            //_out.Write(buffer, 1, pic0_size);
                        }
                        else
                        {
                            Console.WriteLine("Writing pic0.png...\n");

                            using (var t = new FileStream(convertInfo.pic0, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)pic0_size);
                                _out.Write(buffer, 0, (int)pic0_size);
                            }
                        }

                        if (!pic1)
                        {
                            //_base.Seek(base_header[6], SeekOrigin.Begin);
                            //_base.Read(buffer, 0, pic1_size);
                            //_out.Write(buffer, 0, pic1_size);		
                        }
                        else
                        {
                            Console.WriteLine("Writing pic1.png...\n");

                            using (var t = new FileStream(convertInfo.pic1, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)pic1_size);
                                _out.Write(buffer, 0, (int)pic1_size);
                            }
                        }

                        if (snd)
                        {
                            Console.WriteLine("Writing snd0.at3...\n");

                            using (var t = new FileStream(convertInfo.snd0, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)snd_size);
                                _out.Write(buffer, 0, (int)snd_size);
                            }
                        }

                        Console.WriteLine("Writing DATA.PSP...\n");

                        if (prx)
                        {
                            using (var t = new FileStream(convertInfo.data_psp, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)prx_size);
                                _out.Write(buffer, 0, (int)prx_size);
                            }
                        }
                        else
                        {
                            _base.Seek(base_header[8], SeekOrigin.Begin);
                            _base.Read(buffer, 0, (int)prx_size);
                            _out.Write(buffer, 0, (int)prx_size);
                        }


                        offset = (uint)_out.Position;

                        for (i = 0; i < header[9] - offset; i++)
                        {
                            _out.WriteByte(0);
                        }

                        Console.WriteLine("Writing iso header...\n");

                        _out.Write("PSISOIMG0000", 0, 12);

                        p1_offset = (uint)_out.Position;

                        x = isosize + 0x100000;
                        _out.WriteInteger(x, 1);

                        x = 0;
                        for (i = 0; i < 0xFC; i++)
                        {
                            _out.WriteInteger(x, 1);
                        }

                        // TODO
                        var titleBytes = Encoding.ASCII.GetBytes(convertInfo.gameID);
                        Array.Copy(titleBytes, 0, data1, 1, 4);

                        titleBytes = Encoding.ASCII.GetBytes(convertInfo.gameID);
                        Array.Copy(titleBytes, 4, data1, 6, 5);

                        //memcpy(data1 + 1, convertInfo.gameID, 4);
                        //memcpy(data1 + 6, convertInfo.gameID + 4, 5);

                        /*
                            offset = isorealsize/2352+150;
                            min = offset/75/60;
                            sec = (offset-min*60*75)/75;
                            frm = offset-(min*60+sec)*75;
                            data1[0x41b] = bcd(min);
                            data1[0x41c] = bcd(sec);
                            data1[0x41d] = bcd(frm);
                        */
                        if (convertInfo.tocSize > 0)
                        {
                            Console.WriteLine("Copying toc to iso header...\n");
                            // TODO?
                            Array.Copy(convertInfo.tocData, 0, data1, 1024, convertInfo.tocSize);
                            // memcpy(data1 + 1024, convertInfo.tocData, convertInfo.tocSize);

                        }
                        _out.Write(data1, 0, data1.Length);

                        p2_offset = (uint)_out.Position;
                        x = isosize + 0x100000 + 0x2d31;
                        _out.WriteInteger(x, 1);


                        // TODO
                        titleBytes = Encoding.ASCII.GetBytes(convertInfo.gameTitle);
                        Array.Copy(titleBytes, 0, data2, 8, titleBytes.Length);
                        _out.Write(data2, 0, data2.Length);
                        //strcpy((char*)(data2 + 8), convertInfo.gameTitle);
                        //fwrite(data2, 1, sizeof(data2), _out);

                        index_offset = (uint)_out.Position;

                        Console.WriteLine("Writing indexes...\n");

                        // TODO
                        // memset(dummy, 0, sizeof(dummy));

                        offset = 0;

                        if (complevel == 0)
                        {
                            x = 0x9300;
                        }
                        else
                        {
                            x = 0;
                        }

                        for (i = 0; i < isosize / 0x9300; i++)
                        {
                            _out.WriteInteger(offset, 1);
                            _out.WriteInteger(x, 1);
                            _out.Write(dummy, 0, sizeof(uint) * dummy.Length);

                            if (complevel == 0)
                                offset += 0x9300;
                        }

                        offset = (uint)_out.Position;

                        for (i = 0; i < (header[9] + 0x100000) - offset; i++)
                        {
                            _out.WriteByte(0);
                        }

                        Console.WriteLine("Writing iso...\n");

                        totSize = 0;
                        uint bufferSize;

                        if (complevel == 0)
                        {
                            i = 0;
                            if (convertInfo.srcIsPbp)
                            {
                                for (i = 0; i < blockCount; i++)
                                {
                                    buffer2 = ReadBlock(iso_index, (int)i, out bufferSize);

                                    //bufferSize = (uint)buffer2.Length;

                                    if (convertInfo.patchCount > 0) PatchData(buffer2, (int)bufferSize, (int)totSize);
                                    totSize += bufferSize;
                                    if (totSize > isorealsize)
                                    {
                                        bufferSize = bufferSize - (totSize - isorealsize);
                                        totSize = isorealsize;
                                    }

                                    _out.Write(buffer2, 0, (int)bufferSize);

                                    //PostMessage(convertInfo.callback, WM_CONVERT_PROGRESS, 0, totSize);
                                    if (cancelConvert)
                                    {
                                        //if (convertInfo.srcIsPbp) popstripFinal(&iso_index);
                                        return;
                                    }
                                }
                            }
                            else
                            {
                                while ((x = (uint)_in.Read(buffer, 0, 1048576)) > 0)
                                {
                                    if (convertInfo.patchCount > 0) PatchData(buffer, (int)x, (int)i);
                                    _out.Write(buffer, 0, (int)x);

                                    i += x;
                                    //PostMessage(convertInfo.callback, WM_CONVERT_PROGRESS, 0, i);
                                    if (cancelConvert)
                                    {
                                        //fclose(_in);
                                        //fclose(_out);
                                        //fclose(_base);

                                        return;
                                    }
                                }
                            }

                            for (i = 0; i < (isosize - isorealsize); i++)
                            {
                                _out.WriteByte(0);
                            }
                        }

                        else
                        {
                            indexes = new IsoIndex[isosize / 0x9300];

                            //if (!indexes)
                            //{
                            //    if (convertInfo.srcIsPbp) popstripFinal(&iso_index);

                            //    throw new Exception("Cannot alloc memory for indexes!\n");
                            //}

                            i = 0;
                            j = 0;
                            offset = 0;

                            while (true)
                            {

                                if (convertInfo.srcIsPbp)
                                {
                                    if (i >= blockCount) break;
                                    buffer2 = ReadBlock(iso_index, (int)i, out bufferSize);

                                    totSize += bufferSize;
                                    if (totSize > isorealsize)
                                    {
                                        bufferSize = bufferSize - (totSize - isorealsize);
                                        totSize = isorealsize;
                                    }
                                    x = bufferSize;
                                }
                                else
                                {
                                    x = (uint)_in.Read(buffer2, 0, 0x9300);
                                }
                                if (x == 0) break;
                                if (convertInfo.patchCount > 0) PatchData(buffer2, (int)x, (int)j);

                                j += x;

                                //PostMessage(convertInfo.callback, WM_CONVERT_PROGRESS, 0, j);

                                if (cancelConvert)
                                {
                                    //if (convertInfo.srcIsPbp) popstripFinal(&iso_index);
                                    return;
                                }

                                if (x < 0x9300)
                                {
                                    Array.Clear(buffer2, (int)x, (int)(0x9300 - x));
                                    //memset(buffer2 + x, 0, 0x9300 - x);
                                }

                                var cbuffer = Compress(buffer2, complevel);

                                //if (x < 0)
                                //{
                                //    //if (convertInfo.srcIsPbp) popstripFinal(&iso_index);
                                //    throw new Exception("Error _in compression!\n");
                                //}

                                //memset(&indexes[i], 0, sizeof(IsoIndex));

                                x = (uint)cbuffer.Length;

                                indexes[i] = new IsoIndex();

                                indexes[i].offset = offset;

                                if (x >= 0x9300) /* Block didn't compress */
                                {
                                    indexes[i].length = 0x9300;
                                    _out.Write(buffer2, 0, 0x9300);
                                    offset += 0x9300;
                                }
                                else
                                {
                                    indexes[i].length = x;
                                    _out.Write(cbuffer, 0, (int)x);
                                    offset += x;
                                }

                                i++;
                            }

                            if (i != (isosize / 0x9300))
                            {
                                throw new Exception("Some error happened.\n");
                            }

                            x = (uint)_out.Position;

                            if ((x % 0x10) != 0)
                            {
                                end_offset = x + (0x10 - (x % 0x10));

                                for (i = 0; i < (end_offset - x); i++)
                                {
                                    _out.Write('0');
                                }
                            }
                            else
                            {
                                end_offset = x;
                            }

                            end_offset -= header[9];
                        }

                        Console.WriteLine("Writing special data...\n");

                        _base.Seek(base_header[9] + 12, SeekOrigin.Begin);

                        var temp = new byte[sizeof(uint)];
                        _base.Read(temp, 0, 4);
                        x = BitConverter.ToUInt32(temp, 0);

                        x += 0x50000;

                        _base.Seek(x, SeekOrigin.Begin);
                        _base.Read(buffer, 0, 8);

                        var tempstr = System.Text.Encoding.ASCII.GetString(buffer, 0, 8);

                        if (tempstr != "STARTDAT")
                        {
                            throw new Exception($"Cannot find STARTDAT _in {_sbase}. Not a valid PSX eboot.pbp");
                        }

                        _base.Seek(x + 16, SeekOrigin.Begin);
                        _base.Read(header, 0, 8);
                        _base.Seek(x, SeekOrigin.Begin);
                        _base.Read(buffer, 0, (int)header[0]);

                        if (!boot)
                        {
                            _out.Write(buffer, 0, (int)header[0]);
                            _base.Read(buffer, 0, (int)header[1]);
                            _out.Write(buffer, 0, (int)header[1]);
                        }
                        else
                        {
                            Console.WriteLine("Writing boot.png...\n");

                            //ib[5] = boot_size;
                            var temp_buffer = BitConverter.GetBytes(boot_size);
                            for (var k = 0; k < sizeof(uint); k++)
                            {
                                buffer[5 + k] = temp_buffer[k];
                            }

                            _out.Write(buffer, 0, (int)header[0]);

                            using (var t = new FileStream(convertInfo.boot, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                t.Read(buffer, 0, (int)boot_size);
                                _out.Write(buffer, 0, (int)boot_size);
                            }

                            _base.Read(buffer, 0, (int)header[1]);

                            //ib[5] = boot_size;
                            //fwrite(buffer, 1, header[0], _out);
                            //t = fopen(convertInfo.boot, "rb");
                            //t.Read(buffer, 1, boot_size);
                            //_out.Write(buffer, 1, boot_size);
                            //fclose(t);
                            //fread(buffer, 1, header[1], _base);
                        }

                        while ((x = (uint)_base.Read(buffer, 0, 1048576)) > 0)
                        {
                            _out.Write(buffer, 0, (int)x);
                        }

                        if (complevel != 0)
                        {
                            Console.WriteLine("Updating compressed indexes...\n");

                            _out.Seek(p1_offset, SeekOrigin.Begin);
                            _out.WriteInteger(end_offset, 1);

                            end_offset += 0x2d31;
                            _out.Seek(p2_offset, SeekOrigin.Begin);
                            _out.WriteInteger(end_offset, 1);

                            _out.Seek(index_offset, SeekOrigin.Begin);
                            _out.Write(indexes, 0, (int)(4 + 4 + (6 * 4) * (isosize / 0x9300)));
                        }

                    }

                    // PostMessage(convertInfo.callback, WM_CONVERT_DONE, 0, 0_out.Write(indexes

                }

            }
        }
    }
}