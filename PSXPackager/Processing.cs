﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using DiscUtils.Iso9660;
using Popstation;
using Popstation.Cue;
using Popstation.M3u;
using SevenZipExtractor;

namespace PSXPackager
{
    public class Processing
    {
        private readonly ConsoleNotifications _notifications;
        private bool _overwriteIfExists;
        private bool _cancelled;

        public bool Cancelled => _cancelled;

        public Processing()
        {
            _notifications = new ConsoleNotifications
            {
                OverwriteAllSelected = () => _overwriteIfExists = true,
                CancelSelected = () => _cancelled = true
            };
        }

        public bool ProcessFile(
            string file,
            string outPath,
            string tempPath,
            string discs,
            int compressionLevel,
            bool checkIfFileExists,
            CancellationToken cancellationToken)
        {
            bool result = true;

            List<string> tempFiles = null;

            if (_cancelled) return false;

            checkIfFileExists = !_overwriteIfExists && checkIfFileExists;

            try
            {
                if (FileExtensionHelper.IsArchive(file))
                {
                    tempFiles = Unpack(file, tempPath, cancellationToken);

                    if (cancellationToken.IsCancellationRequested) return false;

                    file = "";

                    if (tempFiles.Count(FileExtensionHelper.IsImageFile) == 0)
                    {
                        Console.WriteLine("No image files found!");
                        return false;
                    }
                    else if (tempFiles.Count(FileExtensionHelper.IsImageFile) == 1)
                    {
                        file = tempFiles.FirstOrDefault(FileExtensionHelper.IsImageFile);
                    }
                    else if (tempFiles.Count(FileExtensionHelper.IsBin) > 1)
                    {
                        Console.WriteLine($"Multi-bin image was found!");

                        var cue = tempFiles.FirstOrDefault(FileExtensionHelper.IsCue);
                        if (cue != null)
                        {
                            file = cue;
                        }
                        else
                        {
                            Console.WriteLine($"No cue sheet found! Aborting...");
                            return false;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(file))
                {

                    if (FileExtensionHelper.IsPbp(file))
                    {
                        ExtractPbp(file, outPath, discs, checkIfFileExists, cancellationToken);
                    }
                    else
                    {
                        if (FileExtensionHelper.IsCue(file))
                        {
                            tempFiles = new List<string>();
                            var (outfile, srcToc) = ProcessCue(file, tempPath, tempFiles);
                            result = ConvertIso(outfile, srcToc, outPath, compressionLevel, checkIfFileExists, cancellationToken);
                        }
                        else if (FileExtensionHelper.IsM3u(file))
                        {
                            tempFiles = new List<string>();
                            var filePath = Path.GetDirectoryName(file);
                            var files = new List<string>();
                            var tocs = new List<string>();
                            var m3UFile = M3uFileReader.Read(file);
                            foreach (var fileEntry in m3UFile.FileEntries)
                            {
                                if (FileExtensionHelper.IsCue(fileEntry))
                                {
                                    var (outfile, srcToc) = ProcessCue(Path.Combine(filePath, fileEntry), tempPath, tempFiles);
                                    files.Add(outfile);
                                    tocs.Add(srcToc);
                                }
                                else
                                {
                                    files.Add(Path.Combine(filePath, fileEntry));
                                }
                            }
                            result = ConvertIsos(files.ToArray(), tocs.ToArray(), outPath, compressionLevel, checkIfFileExists, cancellationToken);
                        }
                        else
                        {
                            result = ConvertIso(file, "", outPath, compressionLevel, checkIfFileExists, cancellationToken);
                        }

                    }


                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("Conversion cancelled");
                    return false;
                }

            }
            catch (CancellationException ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
            finally
            {
                Console.CursorVisible = true;

                if (tempFiles != null)
                {
                    foreach (var tempFile in tempFiles)
                    {
                        File.Delete(tempFile);
                    }
                }
            }

            return result;
        }

        static (string, string) ProcessCue(string file, string tempPath, List<string> tempFiles)
        {
            var filePath = Path.GetDirectoryName(file);

            var cueFiles = CueFileReader.Read(file);
            string srcToc = null;
            if (cueFiles.FileEntries.Count() > 1)
            {
                var mergedBin = MergeBins(file, cueFiles, tempPath);
                var cueFile = Path.Combine(tempPath,
                    Path.GetFileNameWithoutExtension(mergedBin.Path) + ".cue");
                CueFileWriter.Write(mergedBin.CueFile, cueFile);
                srcToc = cueFile;
                file = mergedBin.Path;

                tempFiles.Add(mergedBin.Path);
                tempFiles.Add(cueFile);
            }
            else
            {
                srcToc = file;
                file = Path.Combine(filePath, cueFiles.FileEntries.First().FileName);
            }

            return (file, srcToc);
        }

        static MergedBin MergeBins(string file, CueFile cueFilex, string tempPath)
        {
            var mergedBin = new MergedBin();
            mergedBin.CueFile = new CueFile();

            var cueFilePath = Path.GetDirectoryName(file);

            Console.WriteLine($"Merging .bins...");
            long currentFrame = 0;

            var mergedFilename = Path.GetFileNameWithoutExtension(file) + " - MERGED.bin";

            mergedBin.Path = Path.Combine(tempPath, mergedFilename);

            var mcueFile = new CueFileEntry()
            {
                FileName = mergedFilename,
                FileType = "BINARY",
                Tracks = new List<CueTrack>()
            };

            mergedBin.CueFile.FileEntries.Add(mcueFile);

            using (var joinedFile = new FileStream(mergedBin.Path, FileMode.Create))
            {
                foreach (var cueFileEntry in cueFilex.FileEntries)
                {
                    var binPath = cueFileEntry.FileName;
                    if (Path.GetDirectoryName(binPath) == "" || Path.GetDirectoryName(binPath).StartsWith("..") || Path.GetDirectoryName(binPath).StartsWith("."))
                    {
                        binPath = Path.Combine(cueFilePath, cueFileEntry.FileName);
                    }

                    using (var srcStream = new FileStream(binPath, FileMode.Open))
                    {
                        srcStream.CopyTo(joinedFile);

                        foreach (var item in cueFileEntry.Tracks)
                        {
                            var indexes = new List<CueIndex>();
                            foreach (var idx in item.Indexes)
                            {
                                var newIndex = new CueIndex
                                {
                                    Number = idx.Number,
                                    Position = idx.Position + Helper.PositionFromFrames(currentFrame)
                                };
                                indexes.Add(newIndex);
                            }
                            var newTrack = new CueTrack()
                            {
                                DataType = item.DataType,
                                Number = item.Number,
                                Indexes = indexes
                            };
                            mcueFile.Tracks.Add(newTrack);
                        }

                        var frames = srcStream.Length / 2352;
                        currentFrame += frames;
                    }

                }
            }

            return mergedBin;
        }

        static string Shorten(ulong size)
        {
            switch (size)
            {
                case var _ when size >= 1048576:
                    return $"{size / 1048576}MB";
                case var _ when size > 1024:
                    return $"{size / 1024}KB";
                default:
                    return $"{size}B";
            }
        }

        static List<string> Unpack(string file, string tempPath, CancellationToken cancellationToken)
        {
            var files = new List<string>();

            using (ArchiveFile archiveFile = new ArchiveFile(file))
            {
                //var unpackTasks = new List<Task>();
                foreach (Entry entry in archiveFile.Entries)
                {
                    if (FileExtensionHelper.IsImageFile(entry.FileName) || FileExtensionHelper.IsCue(entry.FileName))
                    {
                        Console.WriteLine($"Extracting {entry.FileName} ({Shorten(entry.Size)})");
                        var path = Path.Combine(tempPath, entry.FileName);
                        // extract to file
                        files.Add(path);
                        entry.Extract(path, false);

                        //unpackTasks.Add(Task.Run(() =>
                        //{
                        //    entry.Extract(path, false);
                        //    files.Add(path);
                        //}, cancellationToken));
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return files;
                    }
                }

                //Task.WaitAll(unpackTasks.ToArray());

            }

            return files;
        }

        private const int RING_BUFFER_SIZE = 14;

        string[] gamecodes =
        {
            "SCUS",
            "SLUS",
            "SLES",
            "SCES",
            "SCED",
            "SLPS",
            "SLPM",
            "SCPS",
            "SLED",
            "SLPS",
            "SIPS",
            "ESPM",
            "PBPX",
            "LSP" // This must remain last
        };



        //static GameEntry FindGameInfo(string srcIso)
        //{
        //    GameEntry game = null;
        //    string gameId;

        //    using (var stream = new FileStream(srcIso, FileMode.Open))
        //    {
        //    }

        //    //            char rbuf[RING_BUFFER_SIZE];
        //    //            int bo = 0; // Buffer offset
        //    ////            #define RBI(i) rbuf[(bo+i)%RING_BUFFER_SIZE]
        //    //            // Prime the buffer
        //    //            if ((fread(rbuf, 1, RING_BUFFER_SIZE, file)) != RING_BUFFER_SIZE)
        //    //            {
        //    //                fclose(file);
        //    //                return NULL;
        //    //            }
        //    //            while (1)
        //    //            {
        //    //                // Look for end of potential gameid pattern
        //    //                if (RBI(12) == ';' && RBI(13) == '1')
        //    //                {
        //    //                    // If found, copy buffer into a regular array for comparisons
        //    //                    char buf[RING_BUFFER_SIZE];
        //    //                    for (int c = 0; c < RING_BUFFER_SIZE; c++)
        //    //                        buf[c] = RBI(c);
        //    //                    // Then look for game codes
        //    //                    char* matching_code;
        //    //                    if ((matching_code = MatchingGameCode(buf)))
        //    //                    {
        //    //                        // If found, copy matching game code to output
        //    //                        int code_len = strlen(matching_code);
        //    //                        strncpy(output, matching_code, code_len);
        //    //                        // Next, extract numeric portion.  Skip non-digits; stop if
        //    //                        // gameid reaches 9 characters in length.
        //    //                        int i, j;
        //    //                        for (i = code_len, j = code_len; i < 9 && j != ';'; j++)
        //    //                            if (isdigit(buf[j]))
        //    //                                output[i++] = buf[j];
        //    //                        // If we found enough digits, return the complete gameid.
        //    //                        if (i == 9)
        //    //                        {
        //    //                            output[i] = '\0';
        //    //                            fclose(file);
        //    //                            return output;
        //    //                        }
        //    //                    }
        //    //                }
        //    //                // Read another character and write it into the ring buffer.
        //    //                int c = fgetc(file);
        //    //                if (c == EOF)
        //    //                    break;
        //    //                rbuf[bo++] = c;
        //    //                bo %= RING_BUFFER_SIZE;
        //    //            }

        //    //            fclose(file);
        //    //            return NULL;

        //    var gameDB = new GameDB(Path.Combine(ApplicationInfo.AppPath, "Resources", "gameinfo.db"));

        //    game = gameDB.GetEntryByScannerID(gameId);

        //    if (game != null)
        //    {
        //        Console.WriteLine($"Found {game.GameName}!");
        //    }
        //    else
        //    {
        //        Console.WriteLine($"Could not find gameId {gameId}!");
        //        return null;
        //    }

        //    return game;
        //}

        static GameEntry FindGameInfo(string srcIso)
        {
            var regex = new Regex("(S[LC]\\w{2})[_-](\\d{3})\\.(\\d{2})");
            var bootRegex = new Regex("BOOT\\s*=\\s*cdrom:\\\\?(?:.*?\\\\)?(S[LC]\\w{2}[_-]?\\d{3}\\.\\d{2});1");

            GameEntry game = null;

            using (var stream = new FileStream(srcIso, FileMode.Open))
            {
                var cdReader = new CDReader(stream, false, 2352);

                string gameId = "";

                //foreach (var file in cdReader.GetFiles("\\"))
                //{
                //    var filename = file.Substring(1, file.LastIndexOf(";"));
                //    var match = regex.Match(filename);
                //    if (match.Success)
                //    {
                //        gameId = $"{match.Groups[1].Value}{match.Groups[2].Value}{match.Groups[3].Value}";
                //        break;
                //    }
                //}
                var syscnfFound = false;

                foreach (var file in cdReader.GetFiles("\\"))
                {
                    var filename = file.Substring(1, file.LastIndexOf(";") - 1);
                    if (filename == "SYSTEM.CNF")
                    {
                        syscnfFound = true;
                        using (var datastream = cdReader.OpenFile(file, FileMode.Open))
                        {
                            datastream.Seek(24, SeekOrigin.Begin);
                            var textReader = new StreamReader(datastream);
                            var bootLine = textReader.ReadLine();
                            var bootmatch = bootRegex.Match(bootLine);
                            if (bootmatch.Success)
                            {
                                var match = regex.Match(bootmatch.Groups[1].Value);
                                if (match.Success)
                                {
                                    gameId = $"{match.Groups[1].Value}{match.Groups[2].Value}{match.Groups[3].Value}";
                                    break;
                                }
                            }
                        }
                    }
                }

                if (syscnfFound)
                {
                    var gameDB = new GameDB(Path.Combine(ApplicationInfo.AppPath, "Resources", "gameinfo.db"));

                    game = gameDB.GetEntryByScannerID(gameId);

                    if (game == null)
                    {
                        Console.WriteLine($"Could not find gameId {gameId}!");
                    }
                }
                else
                {
                    Console.WriteLine($"Could not find SYSTEM.CNF!");
                    return null;
                }

            }

            return game;
        }

        private bool ConvertIsos(
            string[] srcIsos,
            string[] srcTocs,
            string outpath,
            int compressionLevel,
            bool checkIfFileExists,
            CancellationToken cancellationToken)
        {
            var game = FindGameInfo(srcIsos[0]);

            if (game != null)
            {
                Console.WriteLine($"Found \"{game.SaveDescription}\"");
            }


            var appPath = ApplicationInfo.AppPath;

            var info = new ConvertIsoInfo()
            {

                DestinationPbp = Path.Combine(outpath, $"{game.SaveDescription}.PBP"),
                DiscInfos = new List<DiscInfo>(),
                MainGameTitle = game.SaveDescription,
                MainGameID = game.SaveFolderName,
                SaveTitle = game.SaveDescription,
                SaveID = game.SaveFolderName,
                Pic0 = Path.Combine(appPath, "Resources", "PIC0.PNG"),
                Pic1 = Path.Combine(appPath, "Resources", "PIC1.PNG"),
                Icon0 = Path.Combine(appPath, "Resources", "ICON0.PNG"),
                BasePbp = Path.Combine(appPath, "Resources", "BASE.PBP"),
                CompressionLevel = compressionLevel,
                CheckIfFileExists = checkIfFileExists

            };

            for (int i = 0; i < srcIsos.Length; i++)
            {
                info.DiscInfos.Add(new DiscInfo()
                {
                    GameID = game.ScannerID,
                    GameTitle = game.GameName,
                    SourceIso = srcIsos[i],
                    SourceToc = i < srcTocs.Length ? srcTocs[i] : "",
                });
            }

            var popstation = new Popstation.Popstation();
            popstation.ActionIfFileExists = _notifications.ActionIfFileExists;
            popstation.OnEvent = _notifications.Notify;

            return popstation.Convert(info, cancellationToken);
        }

        private bool ConvertIso(
            string srcIso,
            string srcToc,
            string outpath,
            int compressionLevel,
            bool checkIfFileExists,
            CancellationToken cancellationToken)
        {
            var game = FindGameInfo(srcIso);
            var appPath = ApplicationInfo.AppPath;

            if (game != null)
            {
                Console.WriteLine($"Found \"{game.SaveDescription}\"");
            }

            var info = new ConvertIsoInfo()
            {
                DiscInfos = new List<DiscInfo>()
                {
                    new DiscInfo()
                    {
                        GameID = game.ScannerID,
                        GameTitle = game.GameName,
                        SourceIso = srcIso,
                        SourceToc = srcToc,
                    }
                },
                DestinationPbp = Path.Combine(outpath, $"{game.GameName}.PBP"),
                MainGameTitle = game.GameName,
                MainGameID = game.SaveFolderName,
                SaveTitle = game.SaveDescription,
                SaveID = game.SaveFolderName,
                Pic0 = Path.Combine(appPath, "Resources", "PIC0.PNG"),
                Pic1 = Path.Combine(appPath, "Resources", "PIC1.PNG"),
                Icon0 = Path.Combine(appPath, "Resources", "ICON0.PNG"),
                BasePbp = Path.Combine(appPath, "Resources", "BASE.PBP"),
                CompressionLevel = compressionLevel,
                CheckIfFileExists = checkIfFileExists
            };

            var popstation = new Popstation.Popstation();
            popstation.ActionIfFileExists = _notifications.ActionIfFileExists;
            popstation.OnEvent = _notifications.Notify;

            return popstation.Convert(info, cancellationToken);
        }

        private void ExtractPbp(
            string srcPbp,
            string outpath,
            string discs,
            bool checkIfFileExists,
            CancellationToken cancellationToken)
        {
            var filename = Path.GetFileNameWithoutExtension(srcPbp) + ".bin";

            var info = new ExtractIsoInfo()
            {
                SourcePbp = srcPbp,
                DestinationIso = Path.Combine(outpath, filename),
                DiscName = "- Disc {0}",
                Discs = string.IsNullOrEmpty(discs) ? Enumerable.Range(1, 5).ToList() : discs.Split(new char[] { ',' }).Select(int.Parse).ToList(),
                CreateCuesheet = true,
                CheckIfFileExists = checkIfFileExists
            };

            var popstation = new Popstation.Popstation();
            popstation.ActionIfFileExists = _notifications.ActionIfFileExists;
            popstation.OnEvent = _notifications.Notify;

            popstation.Extract(info, cancellationToken);
        }
    }
}