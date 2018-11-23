﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NutzCode.CloudFileSystem;
using Shoko.Commons.Extensions;
using Shoko.Commons.Queue;
using Shoko.Models.Enums;
using Shoko.Models.Queue;
using Shoko.Models.Server;
using Shoko.Server.CommandQueue.Commands;
using Shoko.Server.CommandQueue.Commands.AniDB;
using Shoko.Server.CommandQueue.Commands.Hash;
using Shoko.Server.CommandQueue.Commands.Image;
using Shoko.Server.CommandQueue.Commands.Server;
using Shoko.Server.CommandQueue.Commands.Trakt;
using Shoko.Server.CommandQueue.Commands.TvDB;
using Shoko.Server.CommandQueue.Commands.WebCache;
using Shoko.Server.Extensions;
using Shoko.Server.Models;
using Shoko.Server.PlexAndKodi;
using Shoko.Server.Providers.MovieDB;
using Shoko.Server.Providers.TraktTV;
using Shoko.Server.Providers.TvDB;
using Shoko.Server.Providers.WebCache;
using Shoko.Server.Repositories;
using Shoko.Server.Settings;
using Utils = Shoko.Server.Utilities.Utils;

//using Shoko.Server.Commands.Azure;

namespace Shoko.Server.Import
{
    public class Importer
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        public static void RunImport_IntegrityCheck()
        {
            // files which have not been hashed yet
            // or files which do not have a VideoInfo record
            List<SVR_VideoLocal> filesToHash = Repo.Instance.VideoLocal.GetVideosWithoutHash();
            Dictionary<int, SVR_VideoLocal> dictFilesToHash = new Dictionary<int, SVR_VideoLocal>();
            foreach (SVR_VideoLocal vl in filesToHash)
            {
                dictFilesToHash[vl.VideoLocalID] = vl;
                SVR_VideoLocal_Place p = vl.GetBestVideoLocalPlace(true);
                if (p != null)
                {
                    CommandQueue.Queue.Instance.Add(new CmdHashFile(p.FullServerPath, false));
                }
            }

            foreach (SVR_VideoLocal vl in filesToHash)
            {
                // don't use if it is in the previous list
                if (dictFilesToHash.ContainsKey(vl.VideoLocalID)) continue;
                try
                {
                    SVR_VideoLocal_Place p = vl.GetBestVideoLocalPlace(true);
                    if (p != null)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdHashFile(p.FullServerPath, false));
                    }
                }
                catch (Exception ex)
                {
                    string msg = $"Error RunImport_IntegrityCheck XREF: {vl.ToStringDetailed()} - {ex}";
                    logger.Info(msg);
                }
            }

            // files which have been hashed, but don't have an associated episode
            foreach (SVR_VideoLocal v in Repo.Instance.VideoLocal.GetVideosWithoutEpisode()
                .Where(a => !string.IsNullOrEmpty(a.Hash)))
            {
                CommandQueue.Queue.Instance.Add(new CmdServerProcessFile(v.VideoLocalID, false));
            }

            // check that all the episode data is populated
            foreach (SVR_VideoLocal vl in Repo.Instance.VideoLocal.GetAll().Where(a => !string.IsNullOrEmpty(a.Hash)))
            {
                // if the file is not manually associated, then check for AniDB_File info
                SVR_AniDB_File aniFile = Repo.Instance.AniDB_File.GetByHash(vl.Hash);
                foreach (CrossRef_File_Episode xref in vl.EpisodeCrossRefs)
                {
                    if (xref.CrossRefSource != (int) CrossRefSource.AniDB) continue;
                    if (aniFile == null)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdServerProcessFile(vl.VideoLocalID, false));
                    }
                }

                if (aniFile == null) continue;

                // the cross ref is created before the actually episode data is downloaded
                // so lets check for that
                bool missingEpisodes = false;
                foreach (CrossRef_File_Episode xref in aniFile.EpisodeCrossRefs)
                {
                    AniDB_Episode ep = Repo.Instance.AniDB_Episode.GetByEpisodeID(xref.EpisodeID);
                    if (ep == null) missingEpisodes = true;
                }

                if (missingEpisodes)
                {
                    // this will then download the anime etc
                    CommandQueue.Queue.Instance.Add(new CmdServerProcessFile(vl.VideoLocalID, false));
                }
            }
        }

        public static void SyncMedia()
        {
            WebCacheAPI.Instance.AddMediaInfo(Repo.Instance.VideoLocal.GetAll().Select(a=>a.ToMediaRequest()));
        }

        public static void SyncHashes()
        {
            CommandQueue.Queue.Instance.Add(new CmdServerSyncHashes());
        }

        public static void RunImport_ScanFolder(int importFolderID)
        {
            // get a complete list of files
            List<string> fileList = new List<string>();
            int filesFound = 0, videosFound = 0;
            int i = 0;

            try
            {
                SVR_ImportFolder fldr = Repo.Instance.ImportFolder.GetByID(importFolderID);
                if (fldr == null) return;

                // first build a list of files that we already know about, as we don't want to process them again

                List<SVR_VideoLocal_Place> filesAll =
                    Repo.Instance.VideoLocal_Place.GetByImportFolder(fldr.ImportFolderID);
                Dictionary<string, SVR_VideoLocal_Place> dictFilesExisting =
                    new Dictionary<string, SVR_VideoLocal_Place>();
                foreach (SVR_VideoLocal_Place vl in filesAll)
                {
                    try
                    {
                        dictFilesExisting[vl.FullServerPath] = vl;
                    }
                    catch (Exception ex)
                    {
                        string msg = string.Format("Error RunImport_ScanFolder XREF: {0} - {1}", vl.FullServerPath,
                            ex.ToString());
                        logger.Info(msg);
                    }
                }

                logger.Debug("ImportFolder: {0} || {1}", fldr.ImportFolderName, fldr.ImportFolderLocation);
                Utils.GetFilesForImportFolder(fldr.BaseDirectory, ref fileList);

                // Get Ignored Files and remove them from the scan listing
                var ignoredFiles = Repo.Instance.VideoLocal.GetIgnoredVideos().SelectMany(a => a.Places)
                    .Select(a => a.FullServerPath).Where(a => !string.IsNullOrEmpty(a) ).ToList();
                fileList = fileList.Except(ignoredFiles, StringComparer.InvariantCultureIgnoreCase).ToList();

                // get a list of all files in the share
                foreach (string fileName in fileList)
                {
                    i++;

                    if (dictFilesExisting.ContainsKey(fileName))
                    {
                        if (fldr.IsDropSource == 1)
                            dictFilesExisting[fileName].RenameAndMoveAsRequired();
                    }
                    if (fileName.Contains("$RECYCLE.BIN")) continue;

                    filesFound++;
                    logger.Trace("Processing File {0}/{1} --- {2}", i, fileList.Count, fileName);

                    if (!Utils.IsVideo(fileName)) continue;

                    videosFound++;




                    CommandQueue.Queue.Instance.Add(new CmdHashFile(fileName, false));
                }
                logger.Debug("Found {0} new files", filesFound);
                logger.Debug("Found {0} videos", videosFound);
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }
        }

        public static void RunImport_DropFolders()
        {
            // get a complete list of files
            List<string> fileList = new List<string>();
            foreach (SVR_ImportFolder share in Repo.Instance.ImportFolder.GetAll())
            {
                if (!share.FolderIsDropSource) continue;

                logger.Debug("ImportFolder: {0} || {1}", share.ImportFolderName, share.ImportFolderLocation);
                Utils.GetFilesForImportFolder(share.BaseDirectory, ref fileList);
            }

            // Get Ignored Files and remove them from the scan listing
            var ignoredFiles = Repo.Instance.VideoLocal.GetIgnoredVideos().SelectMany(a => a.Places)
                .Select(a => a.FullServerPath).Where(a => !string.IsNullOrEmpty(a)).ToList();
            fileList = fileList.Except(ignoredFiles, StringComparer.InvariantCultureIgnoreCase).ToList();

            // get a list of all the shares we are looking at
            int filesFound = 0, videosFound = 0;
            int i = 0;

            // get a list of all files in the share
            foreach (string fileName in fileList)
            {
                i++;
                if (fileName.Contains("$RECYCLE.BIN")) continue;
                filesFound++;
                logger.Trace("Processing File {0}/{1} --- {2}", i, fileList.Count, fileName);

                if (!Utils.IsVideo(fileName)) continue;

                videosFound++;

                CommandQueue.Queue.Instance.Add(new CmdHashFile(fileName, false));
            }
            logger.Debug("Found {0} files", filesFound);
            logger.Debug("Found {0} videos", videosFound);
        }

        public static void RunImport_NewFiles()
        {
            // first build a list of files that we already know about, as we don't want to process them again
            IReadOnlyList<SVR_VideoLocal_Place> filesAll = Repo.Instance.VideoLocal_Place.GetAll();
            Dictionary<string, SVR_VideoLocal_Place> dictFilesExisting = new Dictionary<string, SVR_VideoLocal_Place>();
            foreach (SVR_VideoLocal_Place vl in filesAll)
            {
                try
                {
                    if (vl.FullServerPath == null)
                    {
                        logger.Info("Invalid File Path found. Removing: " + vl.VideoLocal_Place_ID);
                        vl.RemoveRecord();
                        continue;
                    }
                    dictFilesExisting[vl.FullServerPath] = vl;
                }
                catch (Exception ex)
                {
                    string msg = string.Format("Error RunImport_NewFiles XREF: {0} - {1}",
                        ((vl.FullServerPath ?? vl.FilePath) ?? vl.VideoLocal_Place_ID.ToString()),
                        ex.ToString());
                    logger.Error(msg);
                    //throw;
                }
            }

            // Steps for processing a file
            // 1. Check if it is a video file
            // 2. Check if we have a VideoLocal record for that file
            // .........

            // get a complete list of files
            List<string> fileList = new List<string>();
            foreach (SVR_ImportFolder share in Repo.Instance.ImportFolder.GetAll())
            {
                logger.Debug("ImportFolder: {0} || {1}", share.ImportFolderName, share.ImportFolderLocation);
                try
                {
                    Utils.GetFilesForImportFolder(share.BaseDirectory, ref fileList);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, ex.ToString());
                }
            }

            // get a list fo files that we haven't processed before
            List<string> fileListNew = new List<string>();
            foreach (string fileName in fileList)
            {
                if (fileName.Contains("$RECYCLE.BIN")) continue;
                if (!dictFilesExisting.ContainsKey(fileName))
                    fileListNew.Add(fileName);
            }

            // get a list of all the shares we are looking at
            int filesFound = 0, videosFound = 0;
            int i = 0;

            // get a list of all files in the share
            foreach (string fileName in fileListNew)
            {
                i++;
                filesFound++;
                logger.Trace("Processing File {0}/{1} --- {2}", i, fileList.Count, fileName);

                if (!Utils.IsVideo(fileName)) continue;

                videosFound++;

                CommandQueue.Queue.Instance.Add(new CmdHashFile(fileName, false));
            }
            logger.Debug("Found {0} files", filesFound);
            logger.Debug("Found {0} videos", videosFound);
        }

        public static void RunImport_ImportFolderNewFiles(SVR_ImportFolder fldr)
        {
            List<string> fileList = new List<string>();
            int filesFound = 0, videosFound = 0;
            int i = 0;
            List<SVR_VideoLocal_Place> filesAll = Repo.Instance.VideoLocal_Place.GetByImportFolder(fldr.ImportFolderID);
            Utils.GetFilesForImportFolder(fldr.BaseDirectory, ref fileList);

            HashSet<string> fs = new HashSet<string>(fileList);
            foreach (SVR_VideoLocal_Place v in filesAll)
            {
                if (fs.Contains(v.FullServerPath))
                    fileList.Remove(v.FullServerPath);
            }

            // get a list of all files in the share
            foreach (string fileName in fileList)
            {
                i++;
                if (fileName.Contains("$RECYCLE.BIN")) continue;
                filesFound++;
                logger.Trace("Processing File {0}/{1} --- {2}", i, fileList.Count, fileName);

                if (!Utils.IsVideo(fileName)) continue;

                videosFound++;

                CommandQueue.Queue.Instance.Add(new CmdHashFile(fileName, false));
            }
            logger.Debug("Found {0} files", filesFound);
            logger.Debug("Found {0} videos", videosFound);
        }

        public static void RunImport_GetImages()
        {
            // AniDB posters
            foreach (SVR_AniDB_Anime anime in Repo.Instance.AniDB_Anime.GetAll())
            {
                if (anime.AnimeID == 8580)
                    Console.Write("");

                if (string.IsNullOrEmpty(anime.PosterPath)) continue;

                bool fileExists = File.Exists(anime.PosterPath);
                if (!fileExists)
                {
                    CommandQueue.Queue.Instance.Add(new CmdImageDownloadAllAniDb(anime.AnimeID, false));
                }
            }

            // TvDB Posters
            if (ServerSettings.Instance.TvDB.AutoPosters)
            {
                Dictionary<int, int> postersCount = new Dictionary<int, int>();

                // build a dictionary of series and how many images exist
                IReadOnlyList<TvDB_ImagePoster> allPosters = Repo.Instance.TvDB_ImagePoster.GetAll();
                foreach (TvDB_ImagePoster tvPoster in allPosters)
                {
                    if (string.IsNullOrEmpty(tvPoster.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(tvPoster.GetFullImagePath());

                    if (fileExists)
                    {
                        if (postersCount.ContainsKey(tvPoster.SeriesID))
                            postersCount[tvPoster.SeriesID] = postersCount[tvPoster.SeriesID] + 1;
                        else
                            postersCount[tvPoster.SeriesID] = 1;
                    }
                }

                foreach (TvDB_ImagePoster tvPoster in allPosters)
                {
                    if (string.IsNullOrEmpty(tvPoster.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(tvPoster.GetFullImagePath());

                    int postersAvailable = 0;
                    if (postersCount.ContainsKey(tvPoster.SeriesID))
                        postersAvailable = postersCount[tvPoster.SeriesID];

                    if (!fileExists && postersAvailable < ServerSettings.Instance.TvDB.AutoPostersAmount)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdImageDownload(tvPoster.TvDB_ImagePosterID, ImageEntityType.TvDB_Cover, false));

                        if (postersCount.ContainsKey(tvPoster.SeriesID))
                            postersCount[tvPoster.SeriesID] = postersCount[tvPoster.SeriesID] + 1;
                        else
                            postersCount[tvPoster.SeriesID] = 1;
                    }
                }
            }

            // TvDB Fanart
            if (ServerSettings.Instance.TvDB.AutoFanart)
            {
                Dictionary<int, int> fanartCount = new Dictionary<int, int>();
                IReadOnlyList<TvDB_ImageFanart> allFanart = Repo.Instance.TvDB_ImageFanart.GetAll();
                foreach (TvDB_ImageFanart tvFanart in allFanart)
                {
                    // build a dictionary of series and how many images exist
                    if (string.IsNullOrEmpty(tvFanart.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(tvFanart.GetFullImagePath());

                    if (fileExists)
                    {
                        if (fanartCount.ContainsKey(tvFanart.SeriesID))
                            fanartCount[tvFanart.SeriesID] = fanartCount[tvFanart.SeriesID] + 1;
                        else
                            fanartCount[tvFanart.SeriesID] = 1;
                    }
                }

                foreach (TvDB_ImageFanart tvFanart in allFanart)
                {
                    if (string.IsNullOrEmpty(tvFanart.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(tvFanart.GetFullImagePath());

                    int fanartAvailable = 0;
                    if (fanartCount.ContainsKey(tvFanart.SeriesID))
                        fanartAvailable = fanartCount[tvFanart.SeriesID];

                    if (!fileExists && fanartAvailable < ServerSettings.Instance.TvDB.AutoFanartAmount)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdImageDownload(tvFanart.TvDB_ImageFanartID,ImageEntityType.TvDB_FanArt, false));

                        if (fanartCount.ContainsKey(tvFanart.SeriesID))
                            fanartCount[tvFanart.SeriesID] = fanartCount[tvFanart.SeriesID] + 1;
                        else
                            fanartCount[tvFanart.SeriesID] = 1;
                    }
                }
            }

            // TvDB Wide Banners
            if (ServerSettings.Instance.TvDB.AutoWideBanners)
            {
                Dictionary<int, int> fanartCount = new Dictionary<int, int>();

                // build a dictionary of series and how many images exist
                IReadOnlyList<TvDB_ImageWideBanner> allBanners = Repo.Instance.TvDB_ImageWideBanner.GetAll();
                foreach (TvDB_ImageWideBanner tvBanner in allBanners)
                {
                    if (string.IsNullOrEmpty(tvBanner.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(tvBanner.GetFullImagePath());

                    if (fileExists)
                    {
                        if (fanartCount.ContainsKey(tvBanner.SeriesID))
                            fanartCount[tvBanner.SeriesID] = fanartCount[tvBanner.SeriesID] + 1;
                        else
                            fanartCount[tvBanner.SeriesID] = 1;
                    }
                }

                foreach (TvDB_ImageWideBanner tvBanner in allBanners)
                {
                    if (string.IsNullOrEmpty(tvBanner.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(tvBanner.GetFullImagePath());

                    int bannersAvailable = 0;
                    if (fanartCount.ContainsKey(tvBanner.SeriesID))
                        bannersAvailable = fanartCount[tvBanner.SeriesID];

                    if (!fileExists && bannersAvailable < ServerSettings.Instance.TvDB.AutoWideBannersAmount)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdImageDownload(tvBanner.TvDB_ImageWideBannerID,ImageEntityType.TvDB_Banner, false));

                        if (fanartCount.ContainsKey(tvBanner.SeriesID))
                            fanartCount[tvBanner.SeriesID] = fanartCount[tvBanner.SeriesID] + 1;
                        else
                            fanartCount[tvBanner.SeriesID] = 1;
                    }
                }
            }

            // TvDB Episodes

            foreach (TvDB_Episode tvEpisode in Repo.Instance.TvDB_Episode.GetAll())
            {
                if (string.IsNullOrEmpty(tvEpisode.GetFullImagePath())) continue;
                bool fileExists = File.Exists(tvEpisode.GetFullImagePath());
                if (!fileExists)
                {
                    CommandQueue.Queue.Instance.Add(new CmdImageDownload(tvEpisode.TvDB_EpisodeID, ImageEntityType.TvDB_Episode, false));
                }
            }

            // MovieDB Posters
            if (ServerSettings.Instance.MovieDb.AutoPosters)
            {
                Dictionary<int, int> postersCount = new Dictionary<int, int>();

                // build a dictionary of series and how many images exist
                IReadOnlyList<MovieDB_Poster> allPosters = Repo.Instance.MovieDB_Poster.GetAll();
                foreach (MovieDB_Poster moviePoster in allPosters)
                {
                    if (string.IsNullOrEmpty(moviePoster.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(moviePoster.GetFullImagePath());

                    if (fileExists)
                    {
                        if (postersCount.ContainsKey(moviePoster.MovieId))
                            postersCount[moviePoster.MovieId] = postersCount[moviePoster.MovieId] + 1;
                        else
                            postersCount[moviePoster.MovieId] = 1;
                    }
                }

                foreach (MovieDB_Poster moviePoster in allPosters)
                {
                    if (string.IsNullOrEmpty(moviePoster.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(moviePoster.GetFullImagePath());

                    int postersAvailable = 0;
                    if (postersCount.ContainsKey(moviePoster.MovieId))
                        postersAvailable = postersCount[moviePoster.MovieId];

                    if (!fileExists && postersAvailable < ServerSettings.Instance.MovieDb.AutoPostersAmount)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdImageDownload(moviePoster.MovieDB_PosterID, ImageEntityType.MovieDB_Poster, false));


                        if (postersCount.ContainsKey(moviePoster.MovieId))
                            postersCount[moviePoster.MovieId] = postersCount[moviePoster.MovieId] + 1;
                        else
                            postersCount[moviePoster.MovieId] = 1;
                    }
                }
            }

            // MovieDB Fanart
            if (ServerSettings.Instance.MovieDb.AutoFanart)
            {
                Dictionary<int, int> fanartCount = new Dictionary<int, int>();

                // build a dictionary of series and how many images exist
                IReadOnlyList<MovieDB_Fanart> allFanarts = Repo.Instance.MovieDB_Fanart.GetAll();
                foreach (MovieDB_Fanart movieFanart in allFanarts)
                {
                    if (string.IsNullOrEmpty(movieFanart.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(movieFanart.GetFullImagePath());

                    if (fileExists)
                    {
                        if (fanartCount.ContainsKey(movieFanart.MovieId))
                            fanartCount[movieFanart.MovieId] = fanartCount[movieFanart.MovieId] + 1;
                        else
                            fanartCount[movieFanart.MovieId] = 1;
                    }
                }

                foreach (MovieDB_Fanart movieFanart in Repo.Instance.MovieDB_Fanart.GetAll())
                {
                    if (string.IsNullOrEmpty(movieFanart.GetFullImagePath())) continue;
                    bool fileExists = File.Exists(movieFanart.GetFullImagePath());

                    int fanartAvailable = 0;
                    if (fanartCount.ContainsKey(movieFanart.MovieId))
                        fanartAvailable = fanartCount[movieFanart.MovieId];

                    if (!fileExists && fanartAvailable < ServerSettings.Instance.MovieDb.AutoFanartAmount)
                    {
                        CommandQueue.Queue.Instance.Add(new CmdImageDownload(movieFanart.MovieDB_FanartID, ImageEntityType.MovieDB_FanArt, false));

                        if (fanartCount.ContainsKey(movieFanart.MovieId))
                            fanartCount[movieFanart.MovieId] = fanartCount[movieFanart.MovieId] + 1;
                        else
                            fanartCount[movieFanart.MovieId] = 1;
                    }
                }
            }

            // AniDB Characters
            if (ServerSettings.Instance.AniDb.DownloadCharacters)
            {
                foreach (AniDB_Character chr in Repo.Instance.AniDB_Character.GetAll())
                {
                    if (string.IsNullOrEmpty(chr.GetPosterPath())) continue;
                    bool fileExists = File.Exists(chr.GetPosterPath());
                    if (fileExists) continue;
                    var AnimeID = Repo.Instance.AniDB_Anime_Character.GetByCharID(chr.CharID)?.FirstOrDefault()
                                      ?.AnimeID ?? 0;
                    if (AnimeID == 0) continue;
                    CommandQueue.Queue.Instance.Add(new CmdImageDownloadAllAniDb(AnimeID, false));
                }
            }

            // AniDB Creators
            if (ServerSettings.Instance.AniDb.DownloadCreators)
            {
                foreach (AniDB_Seiyuu seiyuu in Repo.Instance.AniDB_Seiyuu.GetAll())
                {
                    if (string.IsNullOrEmpty(seiyuu.GetPosterPath())) continue;
                    bool fileExists = File.Exists(seiyuu.GetPosterPath());
                    if (fileExists) continue;
                    var chr = Repo.Instance.AniDB_Character_Seiyuu.GetBySeiyuuID(seiyuu.SeiyuuID).FirstOrDefault();
                    if (chr == null) continue;
                    var AnimeID = Repo.Instance.AniDB_Anime_Character.GetByCharID(chr.CharID)?.FirstOrDefault()
                                      ?.AnimeID ?? 0;
                    if (AnimeID == 0) continue;
                    CommandQueue.Queue.Instance.Add(new CmdImageDownloadAllAniDb(AnimeID, false));
                }
            }
        }

        public static void ValidateAllImages()
        {
            Analytics.PostEvent("Management", nameof(ValidateAllImages));
            CommandQueue.Queue.Instance.Add(new CmdServerValidateAllImages());
        }

        public static void RunImport_ScanTvDB()
        {
            Analytics.PostEvent("Management", nameof(RunImport_ScanTvDB));

            TvDBApiHelper.ScanForMatches();
        }

        public static void RunImport_ScanTrakt()
        {
            if (ServerSettings.Instance.TraktTv.Enabled && !string.IsNullOrEmpty(ServerSettings.Instance.TraktTv.AuthToken))
                TraktTVHelper.ScanForMatches();
        }

        public static void RunImport_ScanMovieDB()
        {
            Analytics.PostEvent("Management", nameof(RunImport_ScanMovieDB));
            MovieDBHelper.ScanForMatches();
        }

        public static void RunImport_UpdateTvDB(bool forced)
        {
            Analytics.PostEvent("Management", nameof(RunImport_UpdateTvDB));

            TvDBApiHelper.UpdateAllInfo(forced);
        }

        public static void RunImport_UpdateAllAniDB()
        {
            Analytics.PostEvent("Management", nameof(RunImport_UpdateAllAniDB));

            foreach (SVR_AniDB_Anime anime in Repo.Instance.AniDB_Anime.GetAll())
            {
                CommandQueue.Queue.Instance.Add(new CmdAniDBGetAnimeHTTP(anime.AnimeID, true, false));
            }
        }

        public static void RemoveRecordsWithoutPhysicalFiles()
        {
            logger.Info("Remove Missing Files: Start");
            HashSet<SVR_AnimeEpisode> episodesToUpdate = new HashSet<SVR_AnimeEpisode>();
            HashSet<SVR_AnimeSeries> seriesToUpdate = new HashSet<SVR_AnimeSeries>();
            {
                // remove missing files in valid import folders
                Dictionary<SVR_ImportFolder, List<SVR_VideoLocal_Place>> filesAll = Repo.Instance.VideoLocal_Place.GetAll()
                    .Where(a => a.ImportFolder != null)
                    .GroupBy(a => a.ImportFolder)
                    .ToDictionary(a => a.Key, a => a.ToList());
                foreach (SVR_ImportFolder folder in filesAll.Keys)
                {
                    IFileSystem fs = folder.FileSystem;
                    if (fs == null) continue;

                    foreach (SVR_VideoLocal_Place vl in filesAll[folder])
                    {
                        FileSystemResult<IObject> obj = null;
                        if (!string.IsNullOrWhiteSpace(vl.FullServerPath)) obj = (FileSystemResult<IObject>)fs.Resolve(vl.FullServerPath);
                        if (obj != null && obj.Status == Status.Ok) continue;
                        // delete video local record
                        logger.Info("Removing Missing File: {0}", vl.VideoLocalID);
                        vl.RemoveRecordWithOpenTransaction(episodesToUpdate, seriesToUpdate);
                    }
                }

                List<SVR_VideoLocal> videoLocalsAll = Repo.Instance.VideoLocal.GetAll().ToList();
                // remove empty videolocals
                Repo.Instance.VideoLocal.Delete(videoLocalsAll.Where(a => a.IsEmpty()));

                // Remove duplicate videolocals
                Dictionary<string, List<SVR_VideoLocal>> locals = videoLocalsAll
                    .Where(a => !string.IsNullOrWhiteSpace(a.Hash))
                    .GroupBy(a => a.Hash)
                    .ToDictionary(g => g.Key, g => g.ToList());
                var toRemove = new List<SVR_VideoLocal>();
                var comparer = new VideoLocalComparer();

                foreach (string hash in locals.Keys)
                {
                    List<SVR_VideoLocal> values = locals[hash];
                    values.Sort(comparer);
                    SVR_VideoLocal to = values.First();
                    List<SVR_VideoLocal> froms = values.Where(s => s != to).ToList();
                    foreach (SVR_VideoLocal from in froms)
                    {
                        List<SVR_VideoLocal_Place> places = from.Places;
                        if (places == null || places.Count == 0) continue;
                        {
                            Repo.Instance.VideoLocal_Place.BatchAction(places, places.Count, (place, _) => place.VideoLocalID = to.VideoLocalID);
                        }
                    }
                    toRemove.AddRange(froms);
                }

                Repo.Instance.VideoLocal.Delete(toRemove);

                // Remove files in invalid import folders
                foreach (SVR_VideoLocal v in videoLocalsAll)
                {
                    List<SVR_VideoLocal_Place> places = v.Places;
                    if (v.Places?.Count > 0)
                    {
                        foreach (SVR_VideoLocal_Place place in places)
                        {
                            if (!string.IsNullOrWhiteSpace(place?.FullServerPath)) continue;
                            logger.Info("RemoveRecordsWithOrphanedImportFolder : {0}", v.Info);
                            episodesToUpdate.UnionWith(v.GetAnimeEpisodes());
                            seriesToUpdate.UnionWith(v.GetAnimeEpisodes().Select(a => a.GetAnimeSeries())
                                .DistinctBy(a => a.AnimeSeriesID));
                            Repo.Instance.VideoLocal_Place.Delete(place);
                        }
                    }
                    // Remove duplicate places
                    places = v.Places;
                    if (places?.Count == 1) continue;
                    if (places?.Count > 0)
                    {
                        places = places.DistinctBy(a => a.FullServerPath).ToList();
                        places = v.Places?.Except(places).ToList();
                        Repo.Instance.VideoLocal_Place.Delete(places);
                    }
                    if (v.Places?.Count > 0) continue;
                    // delete video local record
                    logger.Info("RemoveOrphanedVideoLocal : {0}", v.Info);
                    episodesToUpdate.UnionWith(v.GetAnimeEpisodes());
                    seriesToUpdate.UnionWith(v.GetAnimeEpisodes().Select(a => a.GetAnimeSeries())
                        .DistinctBy(a => a.AnimeSeriesID));
                    CommandQueue.Queue.Instance.Add(new CmdAniDBDeleteFileFromMyList(v.MyListID));
                    Repo.Instance.VideoLocal.Delete(v);
                }

                // Clean up failed imports
                Repo.Instance.CrossRef_File_Episode.FindAndDelete(() => Repo.Instance.VideoLocal.GetAll().SelectMany(a => Repo.Instance.CrossRef_File_Episode.GetByHash(a.Hash))
                    .Where(a => Repo.Instance.AniDB_Anime.GetByID(a.AnimeID) == null ||
                                a.GetEpisode() == null).ToList());

                // update everything we modified
                Repo.Instance.AnimeEpisode.BatchAction(episodesToUpdate, episodesToUpdate.Count, (ep, _) =>
                    {
                        if (ep.AnimeEpisodeID == 0)
                        {
                            ep.PlexContract = null;
                        }
                        try
                        {
                            ep.PlexContract = Helper.GenerateVideoFromAnimeEpisode(ep);
                        }
                        catch (Exception ex)
                        {
                            LogManager.GetCurrentClassLogger().Error(ex, ex.ToString());
                        }
                    });
                        
                foreach (SVR_AnimeSeries ser in seriesToUpdate)
                {
                    ser.QueueUpdateStats();
                }
            }
            logger.Info("Remove Missing Files: Finished");
        }

        public static string DeleteCloudAccount(int cloudaccountID)
        {
            
            SVR_CloudAccount cl = Repo.Instance.CloudAccount.GetByID(cloudaccountID);
            if (cl == null) return "Could not find Cloud Account ID: " + cloudaccountID;
            foreach (SVR_ImportFolder f in Repo.Instance.ImportFolder.GetByCloudId(cl.CloudID))
            {
                string r = DeleteImportFolder(f.ImportFolderID);
                if (!string.IsNullOrEmpty(r))
                    return r;
            }
            Repo.Instance.CloudAccount.Delete(cloudaccountID);
            ServerInfo.Instance.RefreshImportFolders();
            ServerInfo.Instance.RefreshCloudAccounts();
            return string.Empty;
        }

        public static string DeleteImportFolder(int importFolderID)
        {
            try
            {
                SVR_ImportFolder ns = Repo.Instance.ImportFolder.GetByID(importFolderID);

                if (ns == null) return "Could not find Import Folder ID: " + importFolderID;

                // first delete all the files attached  to this import folder
                Dictionary<int, SVR_AnimeSeries> affectedSeries = new Dictionary<int, SVR_AnimeSeries>();

                foreach (SVR_VideoLocal_Place vid in Repo.Instance.VideoLocal_Place.GetByImportFolder(importFolderID))
                {
                    //Thread.Sleep(5000);
                    logger.Info("Deleting video local record: {0}", vid.FullServerPath);

                    List<SVR_AnimeEpisode> animeEpisodes = vid.VideoLocal?.GetAnimeEpisodes();
                    if (animeEpisodes?.Count > 0)
                    {
                        var ser = animeEpisodes[0].GetAnimeSeries();
                        if (ser != null && !affectedSeries.ContainsKey(ser.AnimeSeriesID))
                            affectedSeries.Add(ser.AnimeSeriesID, ser);
                    }
                    SVR_VideoLocal v = vid.VideoLocal;
                    // delete video local record
                    logger.Info("RemoveRecordsWithoutPhysicalFiles : {0}", vid.FullServerPath);
                    if (v?.Places.Count == 1)
                    {
                        Repo.Instance.VideoLocal_Place.Delete(vid);
                        Repo.Instance.VideoLocal.Delete(v);
                        CommandQueue.Queue.Instance.Add(new CmdAniDBDeleteFileFromMyList(v.MyListID));
                    }
                    else
                        Repo.Instance.VideoLocal_Place.Delete(vid);
                }

                // delete any duplicate file records which reference this folder
                Repo.Instance.DuplicateFile.FindAndDelete(()=>Repo.Instance.DuplicateFile.GetByImportFolder1(importFolderID));
                Repo.Instance.DuplicateFile.FindAndDelete(()=>Repo.Instance.DuplicateFile.GetByImportFolder2(importFolderID));

                // delete the import folder
                Repo.Instance.ImportFolder.Delete(importFolderID);

                //TODO APIv2: Delete this hack after migration to headless
                //hack until gui id dead
                try
                {
                    Utils.MainThreadDispatch(() =>
                    {
                        ServerInfo.Instance.RefreshImportFolders();
                    });
                }
                catch
                {
                    //dont do this at home :-)
                }

                foreach (SVR_AnimeSeries ser in affectedSeries.Values)
                {
                    ser.QueueUpdateStats();
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
                return ex.Message;
            }
        }

        public static void UpdateAllStats()
        {
            Analytics.PostEvent("Management", "Update All Stats");
            foreach (SVR_AnimeSeries ser in Repo.Instance.AnimeSeries.GetAll())
            {
                ser.QueueUpdateStats();
            }

            foreach (SVR_GroupFilter gf in Repo.Instance.GroupFilter.GetAll())
            {
                gf.QueueUpdate();
            }

            Repo.Instance.GroupFilter.CreateOrVerifyLockedFilters();
        }

        public static int UpdateAniDBFileData(bool missingInfo, bool outOfDate, bool countOnly)
        {
            List<SVR_VideoLocal> vidsToUpdate = new List<SVR_VideoLocal>();
            try
            {
                if (missingInfo)
                {
                    List<SVR_VideoLocal> vids = Repo.Instance.VideoLocal.GetByAniDBResolution("0x0");

                    foreach (SVR_VideoLocal vid in vids)
                    {
                        if (!vidsToUpdate.Any(a=>a.VideoLocalID==vid.VideoLocalID))
                            vidsToUpdate.Add(vid);
                    }

                    vids = Repo.Instance.VideoLocal.GetWithMissingChapters();
                    foreach (SVR_VideoLocal vid in vids)
                    {
                        if (!vidsToUpdate.Any(a => a.VideoLocalID == vid.VideoLocalID))
                            vidsToUpdate.Add(vid);
                    }
                }

                if (outOfDate)
                {
                    List<SVR_VideoLocal> vids = Repo.Instance.VideoLocal.GetByInternalVersion(1);

                    foreach (SVR_VideoLocal vid in vids)
                    {
                        if (!vidsToUpdate.Any(a => a.VideoLocalID == vid.VideoLocalID))
                            vidsToUpdate.Add(vid);
                    }
                }

                if (!countOnly)
                {
                    CommandQueue.Queue.Instance.AddRange(vidsToUpdate.Select(a=>new CmdAniDBGetFile(a, true)));
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, ex.ToString());
            }

            return vidsToUpdate.Count;
        }

        public static void CheckForDayFilters()
        {
            


            using (var upd = Repo.Instance.ScheduledUpdate.BeginAddOrUpdate(() => Repo.Instance.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.DayFiltersUpdate), () => new ScheduledUpdate
            {
                UpdateType = (int)ScheduledUpdateType.DayFiltersUpdate,
                UpdateDetails = string.Empty
            }))
            {
                if (upd.IsUpdate())
                {
                    if (DateTime.Now.Day == upd.Entity.LastUpdate.Day)
                        return;
                }
                upd.Entity.LastUpdate = DateTime.Now;
                upd.Commit();
            }
            //Get GroupFiters that change daily

            HashSet<GroupFilterConditionType> conditions = new HashSet<GroupFilterConditionType>
            {
                GroupFilterConditionType.AirDate,
                GroupFilterConditionType.LatestEpisodeAirDate,
                GroupFilterConditionType.SeriesCreatedDate,
                GroupFilterConditionType.EpisodeWatchedDate,
                GroupFilterConditionType.EpisodeAddedDate
            };
            List<SVR_GroupFilter> evalfilters = Repo.Instance.GroupFilter.GetWithConditionsTypes(conditions)
                .Where(
                    a => a.Conditions.Any(b => conditions.Contains(b.GetConditionTypeEnum()) &&
                                               b.GetConditionOperatorEnum() == GroupFilterOperator.LastXDays))
                .ToList();

            Repo.Instance.GroupFilter.BatchAction(evalfilters, evalfilters.Count, (g, _) => g.CalculateGroupsAndSeries());
        }

        public static void CheckForTvDBUpdates(bool forceRefresh)
        {
            if (ServerSettings.Instance.TvDB.UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh) return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.TvDB.UpdateFrequency);
            List<int> tvDBIDs = new List<int>();
            bool tvDBOnline = false;
            string serverTime = TvDBApiHelper.IncrementalTvDBUpdate(ref tvDBIDs, ref tvDBOnline);

            // update tvdb info every 12 hours




            using (var upd = Repo.Instance.ScheduledUpdate.BeginAddOrUpdate(() => Repo.Instance.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.TvDBInfo), () => new ScheduledUpdate
            {
                UpdateType = (int)ScheduledUpdateType.TvDBInfo
            }))
            {
                if (upd.IsUpdate())
                {
                    // if we have run this in the last 12 hours and are not forcing it, then exit
                    TimeSpan tsLastRun = DateTime.Now - upd.Entity.LastUpdate;
                    if (tsLastRun.TotalHours < freqHours)
                    {
                        if (!forceRefresh) return;
                    }
                }
                upd.Entity.LastUpdate = DateTime.Now;
                upd.Entity.UpdateDetails = serverTime;
                upd.Commit();
            }


            if (tvDBOnline)
            {
                foreach (int tvid in tvDBIDs)
                {
                    // download and update series info, episode info and episode images
                    // will also download fanart, posters and wide banners
                    CommandQueue.Queue.Instance.Add(new CmdTvDBUpdateSeries(tvid, true));
                }
            }

            TvDBApiHelper.ScanForMatches();
        }

        public static void CheckForCalendarUpdate(bool forceRefresh)
        {
            if (ServerSettings.Instance.AniDb.Calendar_UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh)
                return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.AniDb.Calendar_UpdateFrequency);

            // update the calendar every 12 hours
            // we will always assume that an anime was downloaded via http first

            ScheduledUpdate sched =
                Repo.Instance.ScheduledUpdate.GetByUpdateType((int) ScheduledUpdateType.AniDBCalendar);
            if (sched != null)
            {
                // if we have run this in the last 12 hours and are not forcing it, then exit
                TimeSpan tsLastRun = DateTime.Now - sched.LastUpdate;
                if (tsLastRun.TotalHours < freqHours)
                {
                    if (!forceRefresh) return;
                }
            }

            CommandQueue.Queue.Instance.Add(new CmdAniDBGetCalendar(forceRefresh));
        }

        public static void SendUserInfoUpdate(bool forceRefresh)
        {
            // update the anonymous user info every 12 hours
            // we will always assume that an anime was downloaded via http first

            using (var upd = Repo.Instance.ScheduledUpdate.BeginAddOrUpdate(() => Repo.Instance.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.AzureUserInfo), () => new ScheduledUpdate
            {
                UpdateType = (int)ScheduledUpdateType.AzureUserInfo,
                UpdateDetails = string.Empty
            }))
            {
                if (upd.IsUpdate())
                {
                    // if we have run this in the last 6 hours and are not forcing it, then exit
                    TimeSpan tsLastRun = DateTime.Now - upd.Entity.LastUpdate;
                    if (tsLastRun.TotalHours < 6)
                    {
                        if (!forceRefresh) return;
                    }
                }
                upd.Entity.LastUpdate = DateTime.Now;
                upd.Commit();
            }

           // CommandQueue.Queue.Instance.Add(new CmdWebCacheSendUserInfo(ServerSettings.Instance.AniDb.Username));
        }

        public static void CheckForAnimeUpdate(bool forceRefresh)
        {
            if (ServerSettings.Instance.AniDb.Anime_UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh) return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.AniDb.Anime_UpdateFrequency);

            // check for any updated anime info every 12 hours

            ScheduledUpdate sched = Repo.Instance.ScheduledUpdate.GetByUpdateType((int) ScheduledUpdateType.AniDBUpdates);
            if (sched != null)
            {
                // if we have run this in the last 12 hours and are not forcing it, then exit
                TimeSpan tsLastRun = DateTime.Now - sched.LastUpdate;
                if (tsLastRun.TotalHours < freqHours)
                {
                    if (!forceRefresh) return;
                }
            }

            CommandQueue.Queue.Instance.Add(new CmdAniDBGetUpdated(true));
        }

        public static void CheckForMyListStatsUpdate(bool forceRefresh)
        {
            if (ServerSettings.Instance.AniDb.MyListStats_UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh)
                return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.AniDb.MyListStats_UpdateFrequency);

            ScheduledUpdate sched =
                Repo.Instance.ScheduledUpdate.GetByUpdateType((int) ScheduledUpdateType.AniDBMylistStats);
            if (sched != null)
            {
                // if we have run this in the last 24 hours and are not forcing it, then exit
                TimeSpan tsLastRun = DateTime.Now - sched.LastUpdate;
                logger.Trace("Last AniDB MyList Stats Update: {0} minutes ago", tsLastRun.TotalMinutes);
                if (tsLastRun.TotalHours < freqHours)
                {
                    if (!forceRefresh) return;
                }
            }

            CommandQueue.Queue.Instance.Add(new CmdAniDBUpdateMyListStats(forceRefresh));
        }

        public static void CheckForMyListSyncUpdate(bool forceRefresh)
        {
            if (ServerSettings.Instance.AniDb.MyList_UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh) return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.AniDb.MyList_UpdateFrequency);

            // update the calendar every 24 hours

            ScheduledUpdate sched =
                Repo.Instance.ScheduledUpdate.GetByUpdateType((int) ScheduledUpdateType.AniDBMyListSync);
            if (sched != null)
            {
                // if we have run this in the last 24 hours and are not forcing it, then exit
                TimeSpan tsLastRun = DateTime.Now - sched.LastUpdate;
                logger.Trace("Last AniDB MyList Sync: {0} minutes ago", tsLastRun.TotalMinutes);
                if (tsLastRun.TotalHours < freqHours)
                {
                    if (!forceRefresh) return;
                }
            }

            CommandQueue.Queue.Instance.Add(new CmdAniDBSyncMyList(forceRefresh));
        }

        public static void CheckForTraktSyncUpdate(bool forceRefresh)
        {
            if (!ServerSettings.Instance.TraktTv.Enabled) return;
            if (ServerSettings.Instance.TraktTv.SyncFrequency == ScheduledUpdateFrequency.Never && !forceRefresh) return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.TraktTv.SyncFrequency);

            // update the calendar every xxx hours

            ScheduledUpdate sched = Repo.Instance.ScheduledUpdate.GetByUpdateType((int) ScheduledUpdateType.TraktSync);
            if (sched != null)
            {
                // if we have run this in the last xxx hours and are not forcing it, then exit
                TimeSpan tsLastRun = DateTime.Now - sched.LastUpdate;
                logger.Trace("Last Trakt Sync: {0} minutes ago", tsLastRun.TotalMinutes);
                if (tsLastRun.TotalHours < freqHours)
                {
                    if (!forceRefresh) return;
                }
            }

            if (ServerSettings.Instance.TraktTv.Enabled && !string.IsNullOrEmpty(ServerSettings.Instance.TraktTv.AuthToken))
            {
                CommandQueue.Queue.Instance.Add(new CmdTraktSyncCollection(false));
            }
        }

        public static void CheckForTraktAllSeriesUpdate(bool forceRefresh)
        {
            if (!ServerSettings.Instance.TraktTv.Enabled) return;
            if (ServerSettings.Instance.TraktTv.UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh) return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.TraktTv.UpdateFrequency);

            // update the calendar every xxx hours
            ScheduledUpdate sched = Repo.Instance.ScheduledUpdate.GetByUpdateType((int) ScheduledUpdateType.TraktUpdate);
            if (sched != null)
            {
                // if we have run this in the last xxx hours and are not forcing it, then exit
                TimeSpan tsLastRun = DateTime.Now - sched.LastUpdate;
                logger.Trace("Last Trakt Update: {0} minutes ago", tsLastRun.TotalMinutes);
                if (tsLastRun.TotalHours < freqHours)
                {
                    if (!forceRefresh) return;
                }
            }

            CommandQueue.Queue.Instance.Add(new CmdTraktUpdateAllSeries(false));
        }

        public static void CheckForTraktTokenUpdate(bool forceRefresh)
        {
            try
            {
                if (!ServerSettings.Instance.TraktTv.Enabled) return;
                // by updating the Trakt token regularly, the user won't need to authorize again
                int freqHours = 24; // we need to update this daily


                using (var upd = Repo.Instance.ScheduledUpdate.BeginAddOrUpdate(() => Repo.Instance.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.TraktToken), () => new ScheduledUpdate
                {
                    UpdateType = (int)ScheduledUpdateType.TraktToken,
                    UpdateDetails = string.Empty
                }))
                {
                    if (upd.IsUpdate())
                    {
                        // if we have run this in the last xxx hours and are not forcing it, then exit
                        TimeSpan tsLastRun = DateTime.Now - upd.Entity.LastUpdate;
                        logger.Trace("Last Trakt Token Update: {0} minutes ago", tsLastRun.TotalMinutes);
                        if (tsLastRun.TotalHours < freqHours)
                        {
                            if (!forceRefresh) return;
                        }
                    }
                    upd.Entity.LastUpdate = DateTime.Now;
                    upd.Commit();
                }

                TraktTVHelper.RefreshAuthToken();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error in CheckForTraktTokenUpdate: " + ex.ToString());
            }
        }

        public static void CheckForAniDBFileUpdate(bool forceRefresh)
        {
            if (ServerSettings.Instance.AniDb.File_UpdateFrequency == ScheduledUpdateFrequency.Never && !forceRefresh) return;
            int freqHours = Utils.GetScheduledHours(ServerSettings.Instance.AniDb.File_UpdateFrequency);

            // check for any updated anime info every 12 hours

            using (var upd = Repo.Instance.ScheduledUpdate.BeginAddOrUpdate(() => Repo.Instance.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.AniDBFileUpdates), () => new ScheduledUpdate
            {
                UpdateType = (int)ScheduledUpdateType.AniDBFileUpdates,
                UpdateDetails = string.Empty
            }))
            {
                if (upd.IsUpdate())
                {
                    // if we have run this in the last 12 hours and are not forcing it, then exit
                    TimeSpan tsLastRun = DateTime.Now - upd.Entity.LastUpdate;
                    if (tsLastRun.TotalHours < freqHours && !forceRefresh)
                    {
                        return;
                    }
                }
                upd.Entity.LastUpdate = DateTime.Now;
                upd.Commit();
            }
            UpdateAniDBFileData(true, false, false);

            // files which have been hashed, but don't have an associated episode
            List<SVR_VideoLocal> filesWithoutEpisode = Repo.Instance.VideoLocal.GetVideosWithoutEpisode();

            foreach (SVR_VideoLocal vl in filesWithoutEpisode)
            {
                CommandQueue.Queue.Instance.Add(new CmdServerProcessFile(vl.VideoLocalID, true));
            }

            // now check for any files which have been manually linked and are less than 30 days old

        }

        public static void CheckForPreviouslyIgnored()
        {
            //This cannot happens anymore, or we have a corrupt videolocal
        }

        public static void UpdateAniDBTitles()
        {
            int freqHours = 100;

            bool process = false;

            if (!process) return;

            // check for any updated anime info every 100 hours

            using (var upd = Repo.Instance.ScheduledUpdate.BeginAddOrUpdate(() => Repo.Instance.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.AniDBTitles), () => new ScheduledUpdate
            {
                UpdateType = (int)ScheduledUpdateType.AniDBTitles,
                UpdateDetails = string.Empty
            }))
            {
                if (upd.IsUpdate())
                {
                    // if we have run this in the last 100 hours and are not forcing it, then exit
                    TimeSpan tsLastRun = DateTime.Now - upd.Entity.LastUpdate;
                    if (tsLastRun.TotalHours < freqHours) return;
                }
                upd.Entity.LastUpdate = DateTime.Now;
                upd.Commit();
            }
            //CommandQueue.Queue.Instance.Add(new CmdAniDBGetTitles());
        }
    }
}