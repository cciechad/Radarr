using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.MovieImport.Aggregation;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.MovieImport
{
    public interface IMakeImportDecision
    {
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, bool filterExistingFiles);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource);
        List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource, bool filterExistingFiles);
        ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem);
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        private readonly IEnumerable<IImportDecisionEngineSpecification> _specifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAggregationService _aggregationService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDetectSample _detectSample;
        private readonly IParsingService _parsingService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly Logger _logger;

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification> specifications,
                                   IMediaFileService mediaFileService,
                                   IAggregationService aggregationService,
                                   IDiskProvider diskProvider,
                                   IDetectSample detectSample,
                                   IParsingService parsingService,
                                   ICustomFormatCalculationService formatCalculator,
                                   Logger logger)
        {
            _specifications = specifications;
            _mediaFileService = mediaFileService;
            _aggregationService = aggregationService;
            _diskProvider = diskProvider;
            _detectSample = detectSample;
            _parsingService = parsingService;
            _formatCalculator = formatCalculator;
            _logger = logger;
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie)
        {
            return GetImportDecisions(videoFiles, movie, null, null, false);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, bool filterExistingFiles)
        {
            return GetImportDecisions(videoFiles, movie, null, null, false, filterExistingFiles);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource)
        {
            return GetImportDecisions(videoFiles, movie, downloadClientItem, folderInfo, sceneSource, true);
        }

        public List<ImportDecision> GetImportDecisions(List<string> videoFiles, Movie movie, DownloadClientItem downloadClientItem, ParsedMovieInfo folderInfo, bool sceneSource, bool filterExistingFiles)
        {
            var newFiles = filterExistingFiles ? _mediaFileService.FilterExistingFiles(videoFiles.ToList(), movie) : videoFiles.ToList();

            _logger.Debug("Analyzing {0}/{1} files.", newFiles.Count, videoFiles.Count);

            ParsedMovieInfo downloadClientItemInfo = null;

            if (downloadClientItem != null)
            {
                downloadClientItemInfo = Parser.Parser.ParseMovieTitle(downloadClientItem.Title);
            }

            var nonSampleVideoFileCount = GetNonSampleVideoFileCount(newFiles, movie.MovieMetadata);

            var decisions = new List<ImportDecision>();

            foreach (var file in newFiles)
            {
                var localMovie = new LocalMovie
                {
                    Movie = movie,
                    DownloadClientMovieInfo = downloadClientItemInfo,
                    FolderMovieInfo = folderInfo,
                    Path = file,
                    SceneSource = sceneSource,
                    ExistingFile = movie.Path.IsParentPath(file),
                    OtherVideoFiles = nonSampleVideoFileCount > 1
                };

                decisions.AddIfNotNull(GetDecision(localMovie, downloadClientItem, nonSampleVideoFileCount > 1));
            }

            return decisions;
        }

        public ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var reasons = _specifications.SelectMany(c => EvaluateSpec(c, localMovie, downloadClientItem))
                                         .Where(c => c != null);

            foreach (var profile in localMovie.Movie.QualityProfiles.Value)
            {
                if (!reasons.Any(x => x.ProfileId == profile.Id || x.ProfileId == 0))
                {
                    return new ImportDecision(localMovie);
                }
            }

            return new ImportDecision(localMovie, reasons.ToArray());
        }

        private ImportDecision GetDecision(LocalMovie localMovie, DownloadClientItem downloadClientItem, bool otherFiles)
        {
            ImportDecision decision = null;

            var fileMovieInfo = Parser.Parser.ParseMoviePath(localMovie.Path);

            localMovie.FileMovieInfo = fileMovieInfo;
            localMovie.Size = _diskProvider.GetFileSize(localMovie.Path);

            try
            {
                _aggregationService.Augment(localMovie, downloadClientItem);

                if (localMovie.Movie == null)
                {
                    decision = new ImportDecision(localMovie, new Rejection("Invalid movie"));
                }
                else
                {
                    localMovie.CustomFormats = _formatCalculator.ParseCustomFormat(localMovie);
                    localMovie.CustomFormatScore = localMovie.Movie.Profile?.CalculateCustomFormatScore(localMovie.CustomFormats) ?? 0;

                    decision = GetDecision(localMovie, downloadClientItem);
                }
            }
            catch (AugmentingFailedException)
            {
                decision = new ImportDecision(localMovie, new Rejection("Unable to parse file"));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't import file. {0}", localMovie.Path);

                decision = new ImportDecision(localMovie, new Rejection("Unexpected error processing file"));
            }

            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localMovie.Path);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("File rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("File accepted");
            }

            return decision;
        }

        private List<Rejection> EvaluateSpec(IImportDecisionEngineSpecification spec, LocalMovie localMovie, DownloadClientItem downloadClientItem)
        {
            var rejections = new List<Rejection>();

            try
            {
                var results = spec.IsSatisfiedBy(localMovie, downloadClientItem);

                foreach (var result in results.Where(c => !c.Accepted))
                {
                    rejections.Add(new Rejection(result.Reason, result.ProfileId));
                }
            }
            catch (NotImplementedException e)
            {
                _logger.Warn(e, "Spec " + spec.ToString() + " currently does not implement evaluation for movies.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't evaluate decision on {0}", localMovie.Path);
                rejections.Add(new Rejection($"{spec.GetType().Name}: {ex.Message}"));
            }

            return rejections;
        }

        private int GetNonSampleVideoFileCount(List<string> videoFiles, MovieMetadata movie)
        {
            return videoFiles.Count(file =>
            {
                var sample = _detectSample.IsSample(movie, file);

                if (sample == DetectSampleResult.Sample)
                {
                    return false;
                }

                return true;
            });
        }
    }
}
