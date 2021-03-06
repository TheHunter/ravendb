//-----------------------------------------------------------------------
// <copyright file="IndexStorage.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Logging;
using Raven.Abstractions.MEF;
using Raven.Abstractions.Util;
using Raven.Database.Config;
using Raven.Database.Data;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Linq;
using Raven.Database.Plugins;
using Raven.Database.Queries;
using Raven.Database.Storage;
using Raven.Imports.Newtonsoft.Json;
using Raven.Json.Linq;
using Directory = System.IO.Directory;
using System.ComponentModel.Composition;
using System.Security.Cryptography;

namespace Raven.Database.Indexing
{
	/// <summary>
	/// 	Thread safe, single instance for the entire application
	/// </summary>
	public class IndexStorage : CriticalFinalizerObject, IDisposable
	{
	    private readonly DocumentDatabase documentDatabase;
		private const string IndexVersion = "2.0.0.1"; 
		private const string MapReduceIndexVersion = "2.5.0.1";

		private readonly IndexDefinitionStorage indexDefinitionStorage;
		private readonly InMemoryRavenConfiguration configuration;
		private readonly string path;
		private readonly ConcurrentDictionary<string, Index> indexes = new ConcurrentDictionary<string, Index>(StringComparer.OrdinalIgnoreCase);
		private static readonly ILog log = LogManager.GetCurrentClassLogger();
		private static readonly ILog startupLog = LogManager.GetLogger(typeof(IndexStorage).FullName + ".Startup");
		private readonly Analyzer dummyAnalyzer = new SimpleAnalyzer();
		private DateTime latestPersistedQueryTime;
		private readonly FileStream crashMarker;

		public IndexStorage(IndexDefinitionStorage indexDefinitionStorage, InMemoryRavenConfiguration configuration, DocumentDatabase documentDatabase)
		{
		    try
			{
				this.indexDefinitionStorage = indexDefinitionStorage;
				this.configuration = configuration;
				this.documentDatabase = documentDatabase;
				path = configuration.IndexStoragePath;

				if (Directory.Exists(path) == false && configuration.RunInMemory == false)
					Directory.CreateDirectory(path);

				if (configuration.RunInMemory == false)
				{
					var crashMarkerPath = Path.Combine(path, "indexing.crash-marker");

					if (File.Exists(crashMarkerPath))
					{
						// the only way this can happen is if we crashed because of a power outage
						// in this case, we consider all open indexes to be corrupt and force them
						// to be reset. This is because to get better perf, we don't flush the files to disk,
						// so in the case of a power outage, we can't be sure that there wasn't still stuff in
						// the OS buffer that wasn't written yet.
						configuration.ResetIndexOnUncleanShutdown = true;
					}

					// The delete on close ensures that the only way this file will exists is if there was
					// a power outage while the server was running.
					crashMarker = File.Create(crashMarkerPath, 16, FileOptions.DeleteOnClose);
				}

				BackgroundTaskExecuter.Instance.ExecuteAllInterleaved(documentDatabase.WorkContext,
					indexDefinitionStorage.IndexNames, OpenIndexOnStartup);
			}
			catch
			{
				Dispose();
				throw;
			}
		}

	    private void OpenIndexOnStartup(string indexName)
		{
			if (indexName == null) 
                throw new ArgumentNullException("indexName");

			var fixedName = indexDefinitionStorage.FixupIndexName(indexName);

			startupLog.Debug("Loading saved index {0}", indexName);

			var indexDefinition = indexDefinitionStorage.GetIndexDefinition(fixedName);
			if (indexDefinition == null)
				return;

			Index indexImplementation;
			bool resetTried = false;
			bool recoveryTried = false;
			string[] keysToDeleteAfterRecovery = null;
			while (true)
			{
				Lucene.Net.Store.Directory luceneDirectory = null;

				try
				{
					luceneDirectory = OpenOrCreateLuceneDirectory(indexDefinition, createIfMissing: resetTried);
					indexImplementation = CreateIndexImplementation(fixedName, indexDefinition, luceneDirectory);

                    CheckIndexState(luceneDirectory, indexDefinition, indexImplementation, resetTried);

					var simpleIndex = indexImplementation as SimpleIndex; // no need to do this on m/r indexes, since we rebuild them from saved data anyway
					if (simpleIndex != null && keysToDeleteAfterRecovery != null)
					{
						// remove keys from index that were deleted after creating commit point
						simpleIndex.RemoveDirectlyFromIndex(keysToDeleteAfterRecovery, GetLastEtagForIndex(simpleIndex));
					}

					LoadExistingSuggestionsExtentions(fixedName, indexImplementation);

					documentDatabase.TransactionalStorage.Batch(accessor =>
					{
						var indexStats = accessor.Indexing.GetIndexStats(fixedName);
						if (indexStats != null)
							indexImplementation.Priority = indexStats.Priority;

						var read = accessor.Lists.Read("Raven/Indexes/QueryTime", fixedName);
						if (read == null)
						{
							if(IsIdleAutoIndex(indexImplementation))
								indexImplementation.MarkQueried(); // prevent index abandoning right after startup

							return;
						}

						var dateTime = read.Data.Value<DateTime>("LastQueryTime");

						if(IsIdleAutoIndex(indexImplementation) && SystemTime.UtcNow - dateTime > configuration.TimeToWaitBeforeRunningAbandonedIndexes)
							indexImplementation.MarkQueried(); // prevent index abandoning right after startup
						else
							indexImplementation.MarkQueried(dateTime);
						
						if (dateTime > latestPersistedQueryTime)
							latestPersistedQueryTime = dateTime;
					});

					break;
				}
				catch (Exception e)
				{
					if (resetTried)
						throw new InvalidOperationException("Could not open / create index" + indexName + ", reset already tried", e);

					if (recoveryTried == false && luceneDirectory != null)
					{
						recoveryTried = true;
						startupLog.WarnException("Could not open index " + indexName + ". Trying to recover index", e);

						keysToDeleteAfterRecovery = TryRecoveringIndex(fixedName, indexDefinition, luceneDirectory);
					}
					else
					{
						resetTried = true;
						startupLog.WarnException("Could not open index " + indexName + ". Recovery operation failed, forcibly resetting index", e);
						TryResettingIndex(fixedName, indexDefinition);
					}
				}
			}
			indexes.TryAdd(fixedName, indexImplementation);
		}

	    private void CheckIndexState(Lucene.Net.Store.Directory directory, IndexDefinition indexDefinition, Index index, bool resetTried)
	    {
            if (configuration.ResetIndexOnUncleanShutdown == false)
                return;

			// 1. If commitData is null it means that there were no commits, so just in case we are resetting to Etag.Empty
			// 2. If no 'LastEtag' in commitData then we consider it an invalid index
			// 3. If 'LastEtag' is present (and valid), then resetting to it (if it is lower than lastStoredEtag)

			var commitData = IndexReader.GetCommitUserData(directory);

		    if (index.IsMapReduce)
				CheckMapReduceIndexState(commitData, resetTried);
			else
				CheckMapIndexState(commitData, indexDefinition, index);
	    }

		private void CheckMapIndexState(IDictionary<string, string> commitData, IndexDefinition indexDefinition, Index index)
		{
			string value;
			Etag lastEtag = null;
			if (commitData != null && commitData.TryGetValue("LastEtag", out value))
				Etag.TryParse(value, out lastEtag); // etag will be null if parsing will fail

			var lastStoredEtag = GetLastEtagForIndex(index) ?? Etag.Empty;
			lastEtag = lastEtag ?? Etag.Empty;

			if (EtagUtil.IsGreaterThanOrEqual(lastEtag, lastStoredEtag))
				return;

			var now = SystemTime.UtcNow;
			ResetLastIndexedEtag(indexDefinition, lastEtag, now);
		}

		private static void CheckMapReduceIndexState(IDictionary<string, string> commitData, bool resetTried)
		{
			if (resetTried)
				return;

			string marker;
			long commitMarker;
			var valid = commitData != null 
				&& commitData.TryGetValue("Marker", out marker) 
				&& long.TryParse(marker, out commitMarker)
				&& commitMarker == RavenIndexWriter.CommitMarker;

			if (valid == false)
				throw new InvalidOperationException("Map-Reduce index corruption detected.");
		}

		private static bool IsIdleAutoIndex(Index index)
		{
			return index.name.StartsWith("Auto/") && index.Priority == IndexingPriority.Idle;
		}

		private void TryResettingIndex(string indexName, IndexDefinition indexDefinition)
		{
			try
			{
				documentDatabase.TransactionalStorage.Batch(accessor =>
				{
                    accessor.Indexing.DeleteIndex(indexName, documentDatabase.WorkContext.CancellationToken);
					accessor.Indexing.AddIndex(indexName, indexDefinition.IsMapReduce);
				});

				var indexDirectory = indexName;
				var indexFullPath = Path.Combine(path, MonoHttpUtility.UrlEncode(indexDirectory));
				IOExtensions.DeleteDirectory(indexFullPath);

                var suggestionsForIndex = Path.Combine(configuration.IndexStoragePath, "Raven-Suggestions", indexName);
                IOExtensions.DeleteDirectory(suggestionsForIndex);
			}
			catch (Exception exception)
			{
				throw new InvalidOperationException("Could not reset index " + indexName, exception);
			}
		}

		private string[] TryRecoveringIndex(string indexName, IndexDefinition indexDefinition,
											Lucene.Net.Store.Directory luceneDirectory)
		{
			string[] keysToDeleteAfterRecovery = null;
			if (indexDefinition.IsMapReduce == false)
			{
				IndexCommitPoint commitUsedToRestore;

				if (TryReusePreviousCommitPointsToRecoverIndex(luceneDirectory,
															   indexDefinition, path,
															   out commitUsedToRestore,
															   out keysToDeleteAfterRecovery))
				{
					ResetLastIndexedEtag(indexDefinition, commitUsedToRestore.HighestCommitedETag, commitUsedToRestore.TimeStamp);
				}
			}
			else
			{
				RegenerateMapReduceIndex(luceneDirectory, indexDefinition.Name, indexDefinition);
			}
			return keysToDeleteAfterRecovery;
		}

		private void LoadExistingSuggestionsExtentions(string indexName, Index indexImplementation)
		{
			var suggestionsForIndex = Path.Combine(configuration.IndexStoragePath, "Raven-Suggestions", indexName);
			if (!Directory.Exists(suggestionsForIndex))
				return;

		    try
		    {
		        foreach (var directory in Directory.GetDirectories(suggestionsForIndex))
		        {
		            IndexSearcher searcher;
		            using (indexImplementation.GetSearcher(out searcher))
		            {
		                var key = Path.GetFileName(directory);
		                var decodedKey = MonoHttpUtility.UrlDecode(key);
		                var lastIndexOfDash = decodedKey.LastIndexOf('-');
		                var accuracy = float.Parse(decodedKey.Substring(lastIndexOfDash + 1));
		                var lastIndexOfDistance = decodedKey.LastIndexOf('-', lastIndexOfDash - 1);
		                StringDistanceTypes distanceType;
		                Enum.TryParse(decodedKey.Substring(lastIndexOfDistance + 1, lastIndexOfDash - lastIndexOfDistance - 1),
		                              true, out distanceType);
		                var field = decodedKey.Substring(0, lastIndexOfDistance);
		                var extension = new SuggestionQueryIndexExtension(
		                    documentDatabase.WorkContext,
		                    Path.Combine(configuration.IndexStoragePath, "Raven-Suggestions", indexName, key), searcher.IndexReader.Directory() is RAMDirectory,
		                    SuggestionQueryRunner.GetStringDistance(distanceType),
		                    field,
		                    accuracy);
		                indexImplementation.SetExtension(key, extension);
		            }
		        }
		    }
		    catch (Exception e)
		    {
		        log.WarnException("Could not open suggestions for index " + indexName + ", resetting the index", e);
		        try
		        {
		            IOExtensions.DeleteDirectory(suggestionsForIndex);
		        }
		        catch (Exception)
		        {
		            // ignore the failure
		        }
		        throw;
		    }
		}


		protected Lucene.Net.Store.Directory OpenOrCreateLuceneDirectory(
			IndexDefinition indexDefinition,
			string indexName = null,
			bool createIfMissing = true)
		{
			Lucene.Net.Store.Directory directory;
			if (configuration.RunInMemory ||
				(indexDefinition.IsMapReduce == false &&  // there is no point in creating map/reduce indexes in memory, we write the intermediate results to disk anyway
				 indexDefinitionStorage.IsNewThisSession(indexDefinition) &&
				 indexDefinition.DisableInMemoryIndexing == false &&
				 configuration.DisableInMemoryIndexing == false))
			{
				directory = new RAMDirectory();
				new IndexWriter(directory, dummyAnalyzer, IndexWriter.MaxFieldLength.UNLIMITED).Dispose(); // creating index structure
			}
			else
			{
				var indexDirectory = indexName ?? IndexDefinitionStorage.FixupIndexName(indexDefinition.Name, path);
				var indexFullPath = Path.Combine(path, MonoHttpUtility.UrlEncode(indexDirectory));
				directory = new LuceneCodecDirectory(indexFullPath, documentDatabase.IndexCodecs.OfType<AbstractIndexCodec>());

				if (!IndexReader.IndexExists(directory))
				{
					if (createIfMissing == false)
						throw new InvalidOperationException("Index does not exists: " + indexDirectory);

					WriteIndexVersion(directory, indexDefinition);

					//creating index structure if we need to
					new IndexWriter(directory, dummyAnalyzer, IndexWriter.MaxFieldLength.UNLIMITED).Dispose();
				}
				else
				{
					EnsureIndexVersionMatches(indexName, directory, indexDefinition);
					if (directory.FileExists("write.lock"))// force lock release, because it was still open when we shut down
					{
						IndexWriter.Unlock(directory);
						// for some reason, just calling unlock doesn't remove this file
						directory.DeleteFile("write.lock");
					}
					if (directory.FileExists("writing-to-index.lock")) // we had an unclean shutdown
					{
						if (configuration.ResetIndexOnUncleanShutdown)
							throw new InvalidOperationException("Rude shutdown detected on: " + indexDirectory);

						CheckIndexAndTryToFix(directory, indexDirectory);
						directory.DeleteFile("writing-to-index.lock");
					}
				}
			}

			return directory;

		}

		private void RegenerateMapReduceIndex(Lucene.Net.Store.Directory directory, string indexName, IndexDefinition indexDefinition)
		{
			// remove old index data
			var dirOnDisk = Path.Combine(path, MonoHttpUtility.UrlEncode(indexName));
			IOExtensions.DeleteDirectory(dirOnDisk);

			// initialize by new index
			Directory.CreateDirectory(dirOnDisk);
			WriteIndexVersion(directory, indexDefinition);
			new IndexWriter(directory, dummyAnalyzer, IndexWriter.MaxFieldLength.UNLIMITED).Dispose();

			var start = 0;
			const int take = 100;

			documentDatabase.TransactionalStorage.Batch(actions =>
			{
				IList<ReduceTypePerKey> reduceKeysAndTypes;

				do
				{
					reduceKeysAndTypes = actions.MapReduce.GetReduceKeysAndTypes(indexName, start, take).ToList();
					start += take;

					var keysToScheduleOnLevel2 =
						reduceKeysAndTypes.Where(x => x.OperationTypeToPerform == ReduceType.MultiStep).ToList();
					var keysToScheduleOnLevel0 =
						reduceKeysAndTypes.Where(x => x.OperationTypeToPerform == ReduceType.SingleStep).ToList();

					var itemsToScheduleOnLevel2 = keysToScheduleOnLevel2.Select(x => new ReduceKeyAndBucket(0, x.ReduceKey)).ToList();
					var itemsToScheduleOnLevel0 = new List<ReduceKeyAndBucket>();

					foreach (var reduceKey in keysToScheduleOnLevel0.Select(x => x.ReduceKey))
					{
						var mappedBuckets = actions.MapReduce.GetMappedBuckets(indexName, reduceKey).Distinct();

						itemsToScheduleOnLevel0.AddRange(mappedBuckets.Select(x => new ReduceKeyAndBucket(x, reduceKey)));

						actions.General.MaybePulseTransaction();
					}

					foreach (var itemToReduce in itemsToScheduleOnLevel2)
					{
						actions.MapReduce.ScheduleReductions(indexName, 2, itemToReduce);
						actions.General.MaybePulseTransaction();
					}

					foreach (var itemToReduce in itemsToScheduleOnLevel0)
					{
						actions.MapReduce.ScheduleReductions(indexName, 0, itemToReduce);
						actions.General.MaybePulseTransaction();
					}

				} while (reduceKeysAndTypes.Count > 0);
			});
		}

		private void ResetLastIndexedEtag(IndexDefinition indexDefinition, Etag lastIndexedEtag, DateTime timestamp)
		{
			documentDatabase.TransactionalStorage.Batch(
				accessor =>
				accessor.Indexing.UpdateLastIndexed(indexDefinition.Name, lastIndexedEtag, timestamp));
		}

        internal Etag GetLastEtagForIndex(Index index)
        {
	        if (index.IsMapReduce) 
				return null;

            IndexStats stats = null;
            documentDatabase.TransactionalStorage.Batch(accessor => stats = accessor.Indexing.GetIndexStats(index.name));

	        return stats != null ? stats.LastIndexedEtag : Etag.Empty;
        }

		public static string IndexVersionFileName(IndexDefinition indexDefinition)
		{
			if (indexDefinition.IsMapReduce)
				return "mapReduce.version";
			return "index.version";
		}

		public static void WriteIndexVersion(Lucene.Net.Store.Directory directory, IndexDefinition indexDefinition)
		{
			var version = IndexVersion;
			if(indexDefinition.IsMapReduce)
			{
				version = MapReduceIndexVersion;
			}
			using (var indexOutput = directory.CreateOutput(IndexVersionFileName(indexDefinition)))
			{
				indexOutput.WriteString(version);
				indexOutput.Flush();
			}
		}

		private static void EnsureIndexVersionMatches(string indexName, Lucene.Net.Store.Directory directory, IndexDefinition indexDefinition)
		{
			var versionToCheck = IndexVersion;
			if(indexDefinition.IsMapReduce)
			{
				versionToCheck = MapReduceIndexVersion;
			}
			var indexVersion = IndexVersionFileName(indexDefinition);
			if (directory.FileExists(indexVersion) == false)
			{
				throw new InvalidOperationException("Could not find " + indexVersion + " " + indexName + ", resetting index");
			}
			using (var indexInput = directory.OpenInput(indexVersion))
			{
				var versionFromDisk = indexInput.ReadString();
				if (versionFromDisk != versionToCheck)
					throw new InvalidOperationException("Index " + indexName + " is of version " + versionFromDisk +
														" which is not compatible with " + versionToCheck + ", resetting index");
			}
		}

		private static void CheckIndexAndTryToFix(Lucene.Net.Store.Directory directory, string indexDirectory)
		{
			startupLog.Warn("Unclean shutdown detected on {0}, checking the index for errors. This may take a while.", indexDirectory);

			var memoryStream = new MemoryStream();
			var stringWriter = new StreamWriter(memoryStream);
			var checkIndex = new CheckIndex(directory);

			if (startupLog.IsWarnEnabled)
				checkIndex.SetInfoStream(stringWriter);

			var sp = Stopwatch.StartNew();
			var status = checkIndex.CheckIndex_Renamed_Method();
			sp.Stop();
			if (startupLog.IsWarnEnabled)
			{
				startupLog.Warn("Checking index {0} took: {1}, clean: {2}", indexDirectory, sp.Elapsed, status.clean);
				memoryStream.Position = 0;

				log.Warn(new StreamReader(memoryStream).ReadToEnd());
			}

			if (status.clean)
				return;

			startupLog.Warn("Attempting to fix index: {0}", indexDirectory);
			sp.Restart();
			checkIndex.FixIndex(status);
			startupLog.Warn("Fixed index {0} in {1}", indexDirectory, sp.Elapsed);
		}

		public void StoreCommitPoint(string indexName, IndexCommitPoint indexCommit)
		{
			if (indexCommit.SegmentsInfo == null || indexCommit.SegmentsInfo.IsIndexCorrupted)
				return;

			var directoryName = indexCommit.SegmentsInfo.Generation.ToString("0000000000000000000", CultureInfo.InvariantCulture);
			var commitPointDirectory = new IndexCommitPointDirectory(path, indexName, directoryName);

			if (Directory.Exists(commitPointDirectory.AllCommitPointsFullPath) == false)
			{
				Directory.CreateDirectory(commitPointDirectory.AllCommitPointsFullPath);
			}

			Directory.CreateDirectory(commitPointDirectory.FullPath);

			using (var commitPointFile = File.Create(commitPointDirectory.FileFullPath))
			using (var sw = new StreamWriter(commitPointFile))
			{
				var jsonSerializer = new JsonSerializer();
				var textWriter = new JsonTextWriter(sw);

				jsonSerializer.Serialize(textWriter, indexCommit);
				sw.Flush();
			}

			var currentSegmentsFileName = indexCommit.SegmentsInfo.SegmentsFileName;

			File.Copy(Path.Combine(commitPointDirectory.IndexFullPath, currentSegmentsFileName),
						Path.Combine(commitPointDirectory.FullPath, currentSegmentsFileName),
						overwrite: true);

			var storedCommitPoints = Directory.GetDirectories(commitPointDirectory.AllCommitPointsFullPath);

			if (storedCommitPoints.Length > configuration.MaxNumberOfStoredCommitPoints)
			{
				foreach (var toDelete in storedCommitPoints.Take(storedCommitPoints.Length - configuration.MaxNumberOfStoredCommitPoints))
				{
					IOExtensions.DeleteDirectory(toDelete);
				}
			}
		}

		public void AddDeletedKeysToCommitPoints(string indexName, string[] deletedKeys)
		{
			var indexFullPath = Path.Combine(path, MonoHttpUtility.UrlEncode(indexName));

			var existingCommitPoints = IndexCommitPointDirectory.ScanAllCommitPointsDirectory(indexFullPath);

			foreach (var commitPointDirectory in existingCommitPoints.Select(commitPoint => new IndexCommitPointDirectory(path, indexName, commitPoint)))
			{
				using (var stream = File.Open(commitPointDirectory.DeletedKeysFile, FileMode.OpenOrCreate))
				{
					stream.Seek(0, SeekOrigin.End);
					using (var writer = new StreamWriter(stream))
					{
						foreach (var deletedKey in deletedKeys)
						{
							writer.WriteLine(deletedKey);	
						}
					}
				}
			}
		}

		private bool TryReusePreviousCommitPointsToRecoverIndex(Lucene.Net.Store.Directory directory, IndexDefinition indexDefinition, string indexStoragePath, out IndexCommitPoint indexCommit, out string[] keysToDelete)
		{
			indexCommit = null;
			keysToDelete = null;

			if (indexDefinition.IsMapReduce)
				return false;

		    var indexName = indexDefinitionStorage.FixupIndexName(indexDefinition.Name);

			var indexFullPath = Path.Combine(indexStoragePath, MonoHttpUtility.UrlEncode(indexName));

			var allCommitPointsFullPath = IndexCommitPointDirectory.GetAllCommitPointsFullPath(indexFullPath);

			if (Directory.Exists(allCommitPointsFullPath) == false)
				return false;

			var filesInIndexDirectory = Directory.GetFiles(indexFullPath).Select(Path.GetFileName);

			var existingCommitPoints =
				IndexCommitPointDirectory.ScanAllCommitPointsDirectory(indexFullPath);

			Array.Reverse(existingCommitPoints); // start from the highest generation

			foreach (var commitPointDirectoryName in existingCommitPoints)
			{
				try
				{
					var commitPointDirectory = new IndexCommitPointDirectory(indexStoragePath, indexName,
																				commitPointDirectoryName);

					if (TryGetCommitPoint(commitPointDirectory, out indexCommit) == false)
					{
						IOExtensions.DeleteDirectory(commitPointDirectory.FullPath);
						continue; // checksum is invalid, try another commit point
					}

					var missingFile =
						indexCommit.SegmentsInfo.ReferencedFiles.Any(
							referencedFile => filesInIndexDirectory.Contains(referencedFile) == false);

					if (missingFile)
					{
						IOExtensions.DeleteDirectory(commitPointDirectory.FullPath);
						continue; // there are some missing files, try another commit point
					}

					var storedSegmentsFile = indexCommit.SegmentsInfo.SegmentsFileName;

					// here there should be only one segments_N file, however remove all if there is more
					foreach (var currentSegmentsFile in Directory.GetFiles(commitPointDirectory.IndexFullPath, "segments_*"))
					{
						File.Delete(currentSegmentsFile);
					}

					// copy old segments_N file
					File.Copy(Path.Combine(commitPointDirectory.FullPath, storedSegmentsFile),
							  Path.Combine(commitPointDirectory.IndexFullPath, storedSegmentsFile), true);

					try
					{
						// update segments.gen file
						using (var genOutput = directory.CreateOutput(IndexFileNames.SEGMENTS_GEN))
						{
							genOutput.WriteInt(SegmentInfos.FORMAT_LOCKLESS);
							genOutput.WriteLong(indexCommit.SegmentsInfo.Generation);
							genOutput.WriteLong(indexCommit.SegmentsInfo.Generation);
						}
					}
					catch (Exception)
					{
						// here we can ignore, segments.gen is used only as fallback
					}

					if (File.Exists(commitPointDirectory.DeletedKeysFile))
						keysToDelete = File.ReadLines(commitPointDirectory.DeletedKeysFile).ToArray();

					return true;
				}
				catch (Exception ex)
				{
					startupLog.WarnException("Could not recover an index named '" + indexDefinition.Name +
									   "'from segments of the following generation " + commitPointDirectoryName, ex);
				}
			}

			return false;
		}

        public static IndexSegmentsInfo GetCurrentSegmentsInfo(string indexName, Lucene.Net.Store.Directory directory)
        {
            var segmentInfos = new SegmentInfos();
            var result = new IndexSegmentsInfo();

            try
            {
                segmentInfos.Read(directory);

                result.Generation = segmentInfos.Generation;
                result.SegmentsFileName = segmentInfos.GetCurrentSegmentFileName();
                result.ReferencedFiles = segmentInfos.Files(directory, false);
            }
            catch (CorruptIndexException ex)
            {
                log.WarnException(string.Format("Could not read segment information for an index '{0}'", indexName), ex);

                result.IsIndexCorrupted = true;
            }

            return result;
        }

		private static bool TryGetCommitPoint(IndexCommitPointDirectory commitPointDirectory, out IndexCommitPoint indexCommit)
		{
			using (var commitPointFile = File.OpenRead(commitPointDirectory.FileFullPath))
			{
				var jsonSerializer = new JsonSerializer();
				var textReader = new JsonTextReader(new StreamReader(commitPointFile));

				indexCommit = jsonSerializer.Deserialize<IndexCommitPoint>(textReader);

				return indexCommit != null;
			}
		}

		internal Lucene.Net.Store.Directory MakeRAMDirectoryPhysical(RAMDirectory ramDir, string indexName)
		{
			var newDir = new LuceneCodecDirectory(Path.Combine(path, MonoHttpUtility.UrlEncode(IndexDefinitionStorage.FixupIndexName(indexName, path))), documentDatabase.IndexCodecs.OfType<AbstractIndexCodec>());
			Lucene.Net.Store.Directory.Copy(ramDir, newDir, false);
			return newDir;
		}

		private Index CreateIndexImplementation(string directoryPath, IndexDefinition indexDefinition, Lucene.Net.Store.Directory directory)
		{
			var viewGenerator = indexDefinitionStorage.GetViewGenerator(indexDefinition.Name);
			var indexImplementation = indexDefinition.IsMapReduce
										? (Index)new MapReduceIndex(directory, directoryPath, indexDefinition, viewGenerator, documentDatabase.WorkContext)
										: new SimpleIndex(directory, directoryPath, indexDefinition, viewGenerator, documentDatabase.WorkContext);

			configuration.Container.SatisfyImportsOnce(indexImplementation);

			return indexImplementation;
		}

		public string[] Indexes
		{
			get { return indexes.Keys.ToArray(); }
		}

		public bool HasIndex(string index)
		{
			if (index == null)
				return false;
			return indexes.ContainsKey(index);
		}

		public void Dispose()
		{
			var exceptionAggregator = new ExceptionAggregator(log, "Could not properly close index storage");

			exceptionAggregator.Execute(() => Parallel.ForEach(indexes.Values, index => exceptionAggregator.Execute(index.Dispose)));

			exceptionAggregator.Execute(() => dummyAnalyzer.Close());

			exceptionAggregator.Execute(() =>
			{
				if (crashMarker != null)
					crashMarker.Dispose();
			});

			exceptionAggregator.ThrowIfNeeded();
		}

		public void DeleteIndex(string name)
		{
			Index value;
			if (indexes.TryGetValue(name, out value) == false)
			{
				log.Debug("Ignoring delete for non existing index {0}", name);
				return;
			}
			log.Debug("Deleting index {0}", name);
			value.Dispose();
			Index ignored;
			var dirOnDisk = Path.Combine(path, MonoHttpUtility.UrlEncode(name));

			documentDatabase.TransactionalStorage.Batch(accessor =>
				accessor.Lists.Remove("Raven/Indexes/QueryTime", name));

			if (!indexes.TryRemove(name, out ignored) || !Directory.Exists(dirOnDisk))
				return;

			IOExtensions.DeleteDirectory(dirOnDisk);
		}

		public void CreateIndexImplementation(IndexDefinition indexDefinition)
		{
			var fixedName = indexDefinitionStorage.FixupIndexName(indexDefinition.Name);
			log.Debug("Creating index {0} with encoded name {1}", indexDefinition.Name, fixedName);

			IndexDefinitionStorage.ResolveAnalyzers(indexDefinition);
			AssertAnalyzersValid(indexDefinition);

			indexes.AddOrUpdate(fixedName, n =>
			{
				var directory = OpenOrCreateLuceneDirectory(indexDefinition, fixedName);
				return CreateIndexImplementation(fixedName, indexDefinition, directory);
			}, (s, index) => index);
		}

		private static void AssertAnalyzersValid(IndexDefinition indexDefinition)
		{
			foreach (var analyzer in from analyzer in indexDefinition.Analyzers
									 let analyzerType = typeof(StandardAnalyzer).Assembly.GetType(analyzer.Value) ?? Type.GetType(analyzer.Value, throwOnError: false)
									 where analyzerType == null
									 select analyzer)
			{
				throw new ArgumentException(string.Format("Could not create analyzer for field: '{0}' because the type '{1}' was not found", analyzer.Key, analyzer.Value));
			}
		}

		public Query GetLuceneQuery(string index, IndexQuery query, OrderedPartCollection<AbstractIndexQueryTrigger> indexQueryTriggers)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.Debug("Query on non existing index {0}", index);
				throw new InvalidOperationException("Index '" + index + "' does not exists");
			}
			var fieldsToFetch = new FieldsToFetch(new string[0], AggregationOperation.None, null);
			return new Index.IndexQueryOperation(value, query, _ => false, fieldsToFetch, indexQueryTriggers).GetLuceneQuery();
		}

		public IEnumerable<IndexQueryResult> Query(string index, IndexQuery query, Func<IndexQueryResult, bool> shouldIncludeInResults, FieldsToFetch fieldsToFetch, OrderedPartCollection<AbstractIndexQueryTrigger> indexQueryTriggers, CancellationToken token)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.Debug("Query on non existing index '{0}'", index);
				throw new InvalidOperationException("Index '" + index + "' does not exists");
			}

			if ((value.Priority.HasFlag(IndexingPriority.Idle) || value.Priority.HasFlag(IndexingPriority.Abandoned)) &&
				value.Priority.HasFlag(IndexingPriority.Forced) == false)
			{
				documentDatabase.TransactionalStorage.Batch(accessor =>
				{
					value.Priority = IndexingPriority.Normal;
					try
					{
						accessor.Indexing.SetIndexPriority(index, IndexingPriority.Normal);
					}
					catch (Exception e)
					{
						if (accessor.IsWriteConflict(e) == false)
							throw;

						// we explciitly ignore write conflicts here, it is okay if we got set twice (two concurrent queries, or setting while indexing).
					}
					documentDatabase.WorkContext.ShouldNotifyAboutWork(() => "Idle index queried");
					documentDatabase.RaiseNotifications(new IndexChangeNotification()
					{
						Name = value.name,
						Type = IndexChangeTypes.IndexPromotedFromIdle
					});
				});
			}

			var indexQueryOperation = new Index.IndexQueryOperation(value, query, shouldIncludeInResults, fieldsToFetch, indexQueryTriggers);
			if (query.Query != null && query.Query.Contains(Constants.IntersectSeparator))
				return indexQueryOperation.IntersectionQuery(token);


			return indexQueryOperation.Query(token);
		}

		public IEnumerable<RavenJObject> IndexEntires(
			string index,
			IndexQuery query,
			OrderedPartCollection<AbstractIndexQueryTrigger> indexQueryTriggers,
			Reference<int> totalResults)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.Debug("Query on non existing index '{0}'", index);
				throw new InvalidOperationException("Index '" + index + "' does not exists");
			}

			var indexQueryOperation = new Index.IndexQueryOperation(value, query, null, new FieldsToFetch(null, AggregationOperation.None, null), indexQueryTriggers);
			return indexQueryOperation.IndexEntries(totalResults);
		}

		protected internal static IDisposable EnsureInvariantCulture()
		{
			if (Thread.CurrentThread.CurrentCulture == CultureInfo.InvariantCulture)
				return null;

			var oldCurrentCulture = Thread.CurrentThread.CurrentCulture;
			var oldCurrentUiCulture = Thread.CurrentThread.CurrentUICulture;

			Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
			Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
			return new DisposableAction(() =>
			{
				Thread.CurrentThread.CurrentCulture = oldCurrentCulture;
				Thread.CurrentThread.CurrentUICulture = oldCurrentUiCulture;
			});
		}

		public void RemoveFromIndex(string index, string[] keys, WorkContext context)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.Debug("Removing from non existing index '{0}', ignoring", index);
				return;
			}
			value.Remove(keys, context);
			context.RaiseIndexChangeNotification(new IndexChangeNotification
			{
				Name = index,
				Type = IndexChangeTypes.RemoveFromIndex
			});
		}

		public void Index(string index,
			AbstractViewGenerator viewGenerator,
			IndexingBatch batch,
			WorkContext context,
			IStorageActionsAccessor actions,
			DateTime minimumTimestamp)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.Debug("Tried to index on a non existent index {0}, ignoring", index);
				return;
			}
			using (EnsureInvariantCulture())
			using (DocumentCacher.SkipSettingDocumentsInDocumentCache())
			{
				value.IndexDocuments(viewGenerator, batch, actions, minimumTimestamp);
				context.RaiseIndexChangeNotification(new IndexChangeNotification
				{
					Name = index,
					Type = IndexChangeTypes.MapCompleted
				});
			}
		}

		public void Reduce(
			string index,
			AbstractViewGenerator viewGenerator,
			IEnumerable<IGrouping<int, object>> mappedResults,
			int level,
			WorkContext context,
			IStorageActionsAccessor actions,
			HashSet<string> reduceKeys,
			int inputCount)
		{
			Index value;
			if (indexes.TryGetValue(index, out value) == false)
			{
				log.Debug("Tried to index on a non existent index {0}, ignoring", index);
				return;
			}
			var mapReduceIndex = value as MapReduceIndex;
			if (mapReduceIndex == null)
			{
				log.Warn("Tried to reduce on an index that is not a map/reduce index: {0}, ignoring", index);
				return;
			}
			using (EnsureInvariantCulture())
			{
				var reduceDocuments = new MapReduceIndex.ReduceDocuments(mapReduceIndex, viewGenerator, mappedResults, level, context, actions, reduceKeys, inputCount);
				reduceDocuments.ExecuteReduction();
				context.RaiseIndexChangeNotification(new IndexChangeNotification
				{
					Name = index,
					Type = IndexChangeTypes.ReduceCompleted
				});
			}
		}

		internal IndexSearcherHolder.IndexSearcherHoldingState GetCurrentStateHolder(string indexName)
		{
			return GetIndexByName(indexName).GetCurrentStateHolder();
		}

		public IDisposable GetCurrentIndexSearcher(string indexName, out IndexSearcher searcher)
		{
			return GetIndexByName(indexName).GetSearcher(out searcher);
		}

		public IDisposable GetCurrentIndexSearcherAndTermDocs(string indexName, out IndexSearcher searcher, out RavenJObject[] termsDocs)
		{
			return GetIndexByName(indexName).GetSearcherAndTermsDocs(out searcher, out termsDocs);
		}

		private Index GetIndexByName(string indexName)
		{
			var result = indexes.Where(index => String.Compare(index.Key, indexName, StringComparison.OrdinalIgnoreCase) == 0)
				.Select(x => x.Value)
				.FirstOrDefault();
			if (result == null)
				throw new InvalidOperationException(string.Format("Index '{0}' does not exist", indexName));
			return result;
		}

		public void RunIdleOperations()
		{
			foreach (var value in indexes.Values)
			{
				if ((SystemTime.UtcNow - value.LastIndexTime).TotalMinutes < 1)
					continue;

                value.Flush(value.GetLastEtagFromStats());
			}

			SetUnusedIndexesToIdle();
			UpdateLatestPersistedQueryTime();
		}

		private void UpdateLatestPersistedQueryTime()
		{
			documentDatabase.TransactionalStorage.Batch(accessor =>
			{
				var maxDate = latestPersistedQueryTime;
				foreach (var index in indexes)
				{
					var lastQueryTime = index.Value.LastQueryTime ?? DateTime.MinValue;
					if (lastQueryTime <= latestPersistedQueryTime)
						continue;

					accessor.Lists.Set("Raven/Indexes/QueryTime", index.Key, new RavenJObject
	                {
	                    {"LastQueryTime", lastQueryTime}
	                }, UuidType.Indexing);

					if (lastQueryTime > maxDate)
						maxDate = lastQueryTime;
				}
				latestPersistedQueryTime = maxDate;
			});
		}

		public class UnusedIndexState
		{
			public DateTime LastQueryTime { get; set; }
			public Index Index { get; set; }
			public string Name { get; set; }
			public IndexingPriority Priority { get; set; }
			public DateTime CreationDate { get; set; }
		}

		private void SetUnusedIndexesToIdle()
		{
			documentDatabase.TransactionalStorage.Batch(accessor =>
			{
				var autoIndexesSortedByLastQueryTime =
					(from index in indexes
					 let stats = GetIndexStats(accessor, index.Key)
					 where stats != null
					 let lastQueryTime = stats.LastQueryTimestamp ?? DateTime.MinValue
					 where index.Key.StartsWith("Auto/", StringComparison.InvariantCultureIgnoreCase)
					 orderby lastQueryTime
					 select new UnusedIndexState
						 {
							 LastQueryTime = lastQueryTime,
							 Index = index.Value,
							 Name = index.Key,
							 Priority = stats.Priority,
							 CreationDate = stats.CreatedTimestamp
						 }).ToArray();

				var idleChecks = 0;
				for (var i = 0; i < autoIndexesSortedByLastQueryTime.Length; i++)
				{
					var thisItem = autoIndexesSortedByLastQueryTime[i];

					if (thisItem.Priority.HasFlag(IndexingPriority.Disabled) || // we don't really have much to say about those in here
						thisItem.Priority.HasFlag(IndexingPriority.Forced))// if it is forced, we can't change it
						continue;

					var age = (SystemTime.UtcNow - thisItem.CreationDate).TotalMinutes;
					var lastQuery = (SystemTime.UtcNow - thisItem.LastQueryTime).TotalMinutes;

					if (thisItem.Priority.HasFlag(IndexingPriority.Normal))
					{
						var timeToWaitBeforeMarkingAutoIndexAsIdle = documentDatabase.Configuration.TimeToWaitBeforeMarkingAutoIndexAsIdle;
						var timeToWaitForIdleMinutes = timeToWaitBeforeMarkingAutoIndexAsIdle.TotalMinutes * 10;
						if (age < timeToWaitForIdleMinutes)
						{
							HandleActiveIndex(thisItem, age, lastQuery, accessor, timeToWaitForIdleMinutes);
						}
						else if (++idleChecks < 2)
						{
							// If it's a fairly established query then we need to determine whether there is any activity currently
							// If there is activity and this has not been queried against 'recently' it needs idling
							if (i < autoIndexesSortedByLastQueryTime.Length - 1)
							{
								var nextItem = autoIndexesSortedByLastQueryTime[i + 1];
								if ((nextItem.LastQueryTime - thisItem.LastQueryTime).TotalMinutes > timeToWaitForIdleMinutes)
								{
									accessor.Indexing.SetIndexPriority(thisItem.Name, IndexingPriority.Idle);
									thisItem.Index.Priority = IndexingPriority.Idle;
									documentDatabase.RaiseNotifications(new IndexChangeNotification()
									{
										Name = thisItem.Name,
										Type = IndexChangeTypes.IndexDemotedToIdle
									});
								}
							}
						}

						continue;
					}

					if (thisItem.Priority.HasFlag(IndexingPriority.Idle))
					{
						HandleIdleIndex(age, lastQuery, thisItem, accessor);
						continue;
					}
				}
			});
		}

		private IndexStats GetIndexStats(IStorageActionsAccessor accessor, string indexName)
		{
			var indexStats = accessor.Indexing.GetIndexStats(indexName);
			if (indexStats == null)
				return null;
			indexStats.LastQueryTimestamp = GetLastQueryTime(indexName);
			return indexStats;
		}


		private void HandleIdleIndex(double age, double lastQuery, UnusedIndexState thisItem,
											IStorageActionsAccessor accessor)
		{
			// relatively young index, haven't been queried for a while already
			// can be safely removed, probably
			if (age < 90 && lastQuery > 30)
			{
				documentDatabase.DeleteIndex(thisItem.Name);
				return;
			}

			if (lastQuery < configuration.TimeToWaitBeforeMarkingIdleIndexAsAbandoned.TotalMinutes)
				return;

			// old enough, and haven't been queried for a while, mark it as abandoned

			accessor.Indexing.SetIndexPriority(thisItem.Name, IndexingPriority.Abandoned);

			thisItem.Index.Priority = IndexingPriority.Abandoned;

			documentDatabase.RaiseNotifications(new IndexChangeNotification()
			{
				Name = thisItem.Name,
				Type = IndexChangeTypes.IndexDemotedToIdle
			});
		}

		private void HandleActiveIndex(UnusedIndexState thisItem, double age, double lastQuery, IStorageActionsAccessor accessor, double timeToWaitForIdle)
		{
			if (age < (timeToWaitForIdle * 2.5) && lastQuery < (1.5 * timeToWaitForIdle))
				return;

			if (age < (timeToWaitForIdle * 6) && lastQuery < (2.5 * timeToWaitForIdle))
				return;

			accessor.Indexing.SetIndexPriority(thisItem.Name, IndexingPriority.Idle);

			thisItem.Index.Priority = IndexingPriority.Idle;

			documentDatabase.RaiseNotifications(new IndexChangeNotification()
			{
				Name = thisItem.Name,
				Type = IndexChangeTypes.IndexDemotedToIdle
			});
		}

		public void FlushMapIndexes()
		{
			foreach (var value in indexes.Values.Where(value => !value.IsMapReduce))
			{
                value.Flush(value.GetLastEtagFromStats());
			}
		}

		public void FlushReduceIndexes()
		{
			foreach (var value in indexes.Values.Where(value => value.IsMapReduce))
			{
                value.Flush(value.GetLastEtagFromStats());
			}
		}

		public IIndexExtension GetIndexExtension(string index, string indexExtensionKey)
		{
			return GetIndexByName(index).GetExtension(indexExtensionKey);
		}

		public IIndexExtension GetIndexExtensionByPrefix(string index, string indexExtensionKeyPrefix)
		{
			return GetIndexByName(index).GetExtensionByPrefix(indexExtensionKeyPrefix);
		}

		public void SetIndexExtension(string indexName, string indexExtensionKey, IIndexExtension suggestionQueryIndexExtension)
		{
			GetIndexByName(indexName).SetExtension(indexExtensionKey, suggestionQueryIndexExtension);
		}

		public Index GetIndexInstance(string indexName)
		{
			return indexes.Where(index => String.Compare(index.Key, indexName, StringComparison.OrdinalIgnoreCase) == 0)
				.Select(x => x.Value)
				.FirstOrDefault();
		}

		public void MarkCachedQuery(string indexName)
		{
			GetIndexByName(indexName).MarkQueried();
		}

		public DateTime? GetLastQueryTime(string index)
		{
			return GetIndexByName(index).LastQueryTime;
		}

		public IndexingPerformanceStats[] GetIndexingPerformance(string index)
		{
			return GetIndexByName(index).GetIndexingPerformance();
		}

		public void Backup(string directory, string incrementalTag = null)
		{
			Parallel.ForEach(indexes.Values, index =>
				index.Backup(directory, path, incrementalTag));
		}

		public void MergeAllIndexes()
		{
			Parallel.ForEach(indexes.Values, index =>
											 index.MergeSegments());
		}

		public string IndexOnRam(string name)
		{
			return GetIndexByName(name).IsOnRam;
		}

		public void ForceWriteToDisk(string index)
		{
			GetIndexByName(index).ForceWriteToDisk();
		}
	}
}
